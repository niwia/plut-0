using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Pluto.Services;

/// <summary>
/// Verification and repair of Steam <c>appmanifest_&lt;appid&gt;.acf</c> files.
///
/// This used to be regex surgery over the raw VDF text, which had two serious bugs:
///   1. It wrote <c>"manifest" "0"</c> placeholders into live Steam manifests whenever
///      called without real manifest IDs (which is the only way it was ever called).
///      Manifest 0 tells Steam "no manifest", triggering a full re-verify/re-download.
///   2. Its <c>StateFlags</c> repair sat after an early return, so it almost never ran.
///
/// It now parses and re-serialises the VDF properly, and refuses to invent manifest IDs.
/// </summary>
public static class SteamAcfService
{
    /// <summary>
    /// Steam's STATE_FULLY_INSTALLED bit.
    ///
    /// StateFlags is a BITFIELD, not an enum value: real manifests carry combinations
    /// such as 516 (archived + fully installed) and 36 (update stopped + fully installed).
    /// Testing for equality against 4 and overwriting it would silently destroy the other
    /// bits, so we mask instead.
    /// </summary>
    private const int StateFullyInstalledBit = 4;

    /// <summary>
    /// Ensures the manifest is internally consistent so Steam treats the game as installed.
    ///
    /// Only writes <c>InstalledDepots</c> entries when genuine manifest IDs are supplied.
    /// Passing an empty <paramref name="manifests"/> array is safe and leaves the block alone.
    /// </summary>
    /// <param name="acfPath">Path to appmanifest_&lt;appid&gt;.acf.</param>
    /// <param name="appId">Owning AppID.</param>
    /// <param name="depotIds">Depot IDs belonging to this app.</param>
    /// <param name="manifestIds">
    /// Real manifest IDs, positionally aligned with <paramref name="depotIds"/>. May be
    /// empty, in which case <c>InstalledDepots</c> is left untouched.
    /// </param>
    /// <param name="sizeOnDisk">
    /// Total install size in bytes, or 0 to leave the existing value alone. Steam recalculates
    /// this itself during verification, so it never needs to be forced.
    /// </param>
    /// <returns>True if the file was modified.</returns>
    public static bool EnsureAcfIntegrity(
        string acfPath,
        string appId,
        IReadOnlyList<string> depotIds,
        IReadOnlyList<string> manifestIds,
        long sizeOnDisk = 0)
    {
        if (!File.Exists(acfPath))
        {
            PlutoLogger.Warn("Acf", $"Manifest not found, skipping repair: {acfPath}");
            return false;
        }

        try
        {
            var original = File.ReadAllText(acfPath);
            if (string.IsNullOrWhiteSpace(original))
            {
                PlutoLogger.Warn("Acf", $"Manifest {acfPath} is empty; not touching it");
                return false;
            }

            if (!VdfParser.TryParse(original, out var root))
            {
                // Refuse to rewrite a file we cannot fully understand - better to leave
                // a malformed manifest for Steam to complain about than to corrupt it.
                PlutoLogger.Error("Acf", $"Could not parse manifest {acfPath}; leaving it untouched");
                return false;
            }

            var appState = root.Children.TryGetValue("AppState", out var state) ? state : null;
            if (appState == null)
            {
                PlutoLogger.Error("Acf", $"Manifest {acfPath} has no AppState block; leaving it untouched");
                return false;
            }

            var changed = false;

            // 1. Steam must believe the game is fully installed, otherwise it offers to
            //    "verify" a game that is already there.
            //    Set the bit rather than replacing the value, preserving sibling flags.
            var stateFlagsText = appState.GetString("StateFlags");
            var stateFlags = int.TryParse(stateFlagsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;

            if ((stateFlags & StateFullyInstalledBit) == 0)
            {
                var updated = stateFlags | StateFullyInstalledBit;
                appState.SetString("StateFlags", updated.ToString(CultureInfo.InvariantCulture));
                PlutoLogger.Info("Acf", $"Set StateFlags {stateFlagsText ?? "(missing)"} -> {updated} in {acfPath}");
                changed = true;
            }

            // 2. Populate InstalledDepots only when we have genuine manifest IDs.
            //    Writing manifest 0 is worse than leaving the block empty.
            if (manifestIds.Count > 0 && depotIds.Count > 0)
            {
                var installed = appState.Children.TryGetValue("InstalledDepots", out var d) ? d : null;
                if (installed == null || installed.Children.Count == 0)
                {
                    appState.SetBlock("InstalledDepots", BuildInstalledDepots(depotIds, manifestIds));
                    PlutoLogger.Info("Acf", $"Populated InstalledDepots ({depotIds.Count} depots) in {acfPath}");
                    changed = true;
                }
            }

            // 3. Only correct SizeOnDisk when we were given a real measurement.
            //    Zero means "unknown", and forcing 0 makes Steam think the install is empty.
            if (sizeOnDisk > 0 && appState.GetString("SizeOnDisk") != sizeOnDisk.ToString(CultureInfo.InvariantCulture))
            {
                appState.SetString("SizeOnDisk", sizeOnDisk.ToString(CultureInfo.InvariantCulture));
                changed = true;
            }

            if (!changed) return false;

            File.WriteAllText(acfPath, VdfParser.Serialize(root));
            return true;
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("Acf", $"Error repairing manifest {acfPath}", ex);
            return false;
        }
    }

    private static VdfNode BuildInstalledDepots(IReadOnlyList<string> depotIds, IReadOnlyList<string> manifestIds)
    {
        var node = new VdfNode("InstalledDepots");

        for (int i = 0; i < depotIds.Count && i < manifestIds.Count; i++)
        {
            var depotId = depotIds[i];
            var manifestId = manifestIds[i];

            // Skip empty/placeholder manifest IDs outright.
            if (string.IsNullOrWhiteSpace(manifestId) || manifestId == "0") continue;

            var entry = new VdfNode(depotId);
            entry.SetString("manifest", manifestId);
            entry.SetString("size", "0");
            node.Children[depotId] = entry;
        }

        return node;
    }

    /// <summary>
    /// Minimal Valve VDF (KeyValues) node. Supports the subset Steam ACF files use:
    /// quoted strings and nested blocks. Comments and unquoted tokens are tolerated.
    /// </summary>
    internal sealed class VdfNode
    {
        public string Name { get; }
        public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Value { get; internal set; }

        public VdfNode(string name) => Name = name;

        public string? GetString(string key)
        {
            if (Children.TryGetValue(key, out var child) && child.Value != null) return child.Value;

            // Some ACF writers inline simple keys directly on the parent as "key" "value".
            if (InlineValues.TryGetValue(key, out var inline)) return inline;
            return null;
        }

        public void SetString(string key, string value)
        {
            var child = new VdfNode(key) { Value = value };
            Children[key] = child;
        }

        public void SetBlock(string key, VdfNode block)
        {
            Children[key] = block;
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public Dictionary<string, string> InlineValues { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tolerant VDF reader/writer. Deliberately small: ACF is a flat, well-known shape and
    /// pulling in a full parser for it would be more surface area than the format deserves.
    /// </summary>
    internal static class VdfParser
    {
        public static bool TryParse(string text, out VdfNode root)
        {
            root = new VdfNode("root");
            try
            {
                int pos = 0;
                var stack = new Stack<VdfNode>();
                stack.Push(root);

                while (pos < text.Length)
                {
                    SkipWhitespaceAndComments(text, ref pos);
                    if (pos >= text.Length) break;

                    if (text[pos] == '}')
                    {
                        if (stack.Count > 1) stack.Pop();
                        pos++;
                        continue;
                    }

                    if (!TryReadToken(text, ref pos, out var token)) break;

                    SkipWhitespaceAndComments(text, ref pos);

                    // A token followed by '{' opens a block; otherwise it's key/value.
                    if (pos < text.Length && text[pos] == '{')
                    {
                        pos++;
                        var block = new VdfNode(token);
                        stack.Peek().Children[token] = block;
                        stack.Push(block);
                    }
                    else
                    {
                        if (!TryReadToken(text, ref pos, out var value)) break;

                        var leaf = new VdfNode(token) { Value = value };
                        stack.Peek().Children[token] = leaf;
                        stack.Peek().InlineValues[token] = value;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                PlutoLogger.Warn("Acf", $"VDF parse failed: {ex.Message}");
                return false;
            }
        }

        private static void SkipWhitespaceAndComments(string text, ref int pos)
        {
            while (pos < text.Length)
            {
                if (char.IsWhiteSpace(text[pos])) { pos++; continue; }

                if (text[pos] == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
                {
                    while (pos < text.Length && text[pos] != '\n') pos++;
                    continue;
                }

                break;
            }
        }

        private static bool TryReadToken(string text, ref int pos, out string token)
        {
            token = string.Empty;
            if (pos >= text.Length) return false;

            if (text[pos] == '"')
            {
                pos++;
                var sb = new StringBuilder();
                while (pos < text.Length)
                {
                    char c = text[pos];
                    if (c == '\\' && pos + 1 < text.Length)
                    {
                        sb.Append(text[pos + 1]);
                        pos += 2;
                        continue;
                    }
                    if (c == '"') { pos++; token = sb.ToString(); return true; }
                    sb.Append(c);
                    pos++;
                }
                // Unterminated quote - salvage what we have rather than failing outright.
                token = sb.ToString();
                return token.Length > 0;
            }

            // Unquoted token: read until whitespace or a structural character.
            int start = pos;
            while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] != '{' && text[pos] != '}')
                pos++;

            token = text[start..pos];
            return token.Length > 0;
        }

        public static string Serialize(VdfNode root)
        {
            var sb = new StringBuilder();

            // `root` is a synthetic container holding the real top-level blocks
            // (normally just "AppState"). Writing it out as a named block would emit
            // an unnamed brace pair, which is not parseable VDF.
            foreach (var child in root.Children.Values)
            {
                WriteNode(sb, child, 0);
            }

            return sb.ToString();
        }

        private static void WriteNode(StringBuilder sb, VdfNode node, int depth)
        {
            string indent = new('\t', depth);
            bool hasChildren = node.Children.Count > 0;

            // Leaf: "key"		"value"
            if (!hasChildren)
            {
                // A block that was empty in the source (e.g. "MountedConfig" {})
                // has no Value. Emit an empty block rather than an empty string
                // so the shape round-trips faithfully.
                if (node.Value == null)
                {
                    sb.Append(indent).Append('"').Append(Escape(node.Name)).Append('"').Append('\n');
                    sb.Append(indent).Append("{\n").Append(indent).Append("}\n");
                    return;
                }

                sb.Append(indent)
                  .Append('"').Append(Escape(node.Name)).Append('"')
                  .Append("\t\t\"")
                  .Append(Escape(node.Value)).Append('"')
                  .Append('\n');
                return;
            }

            // Block: "key"
            // {
            //     ...
            // }
            sb.Append(indent).Append('"').Append(Escape(node.Name)).Append('"').Append('\n');
            sb.Append(indent).Append("{\n");

            foreach (var kv in node.Children)
            {
                WriteNode(sb, kv.Value, depth + 1);
            }

            sb.Append(indent).Append("}\n");
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}