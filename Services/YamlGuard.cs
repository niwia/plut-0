using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Pluto.Services;

/// <summary>
/// Guardrails for editing SLSsteam's config.yaml, ported from ASSella's
/// <c>yaml_config_manager.py</c>.
///
/// Pluto previously wrote whatever string manipulation produced, straight to disk.
/// That allowed four distinct corruptions, all reproduced against the real
/// service before this class existed:
///
///   1. <b>Line injection.</b> A game name containing a newline was emitted into
///      an inline comment, so the remainder of the name became a new top-level
///      YAML key. A name of "Evil\nInjectKey: yes" wrote a real <c>InjectKey</c>
///      setting into the user's SLSsteam config.
///   2. <b>Duplicate top-level keys.</b> A flow-style section (<c>AdditionalApps: []</c>)
///      was not recognised as a section, so a second <c>AdditionalApps:</c> block was
///      appended. YAML resolves duplicates to the last one, silently discarding the
///      original section contents.
///   3. <b>Unvalidated writes.</b> Nothing checked the result parsed as YAML before
///      committing it, so a mangled edit reached a file SLSsteam reads at startup.
///   4. <b>Unvalidated identifiers.</b> Non-numeric AppIDs and malformed AES keys were
///      written verbatim.
///
/// Nothing here parses YAML into a document and rewrites it, which would destroy
/// comments and formatting that SLSsteam's config relies on. Like ASSella, these are
/// targeted text edits that preserve everything they do not deliberately touch.
/// </summary>
public static class YamlGuard
{
    private static readonly Regex TopLevelKeyPattern =
        new(@"^[A-Za-z0-9_]+[ \t]*:", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex NumericIdPattern =
        new(@"^\d+$", RegexOptions.Compiled);

    private static readonly Regex Hex64Pattern =
        new(@"^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    /// <summary>
    /// Collapses a free-text value into something safe to place after a '#'.
    /// Newlines and control characters become spaces so a comment can never spill
    /// onto the next line and become structure.
    /// </summary>
    public static string SanitizeComment(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return string.Empty;

        var sb = new StringBuilder(comment.Length);
        foreach (var c in comment)
        {
            // Newlines, carriage returns, tabs and control chars must not survive.
            sb.Append(c is '\n' or '\r' or '\t' || char.IsControl(c) ? ' ' : c);
        }

        return Regex.Replace(sb.ToString(), @"[ ]{2,}", " ").Trim();
    }

    /// <summary>
    /// Validates an AppID or DepotID. Returns the trimmed digits, or null when the
    /// value is not purely numeric.
    /// </summary>
    public static string? SanitizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var trimmed = id.Trim();
        return NumericIdPattern.IsMatch(trimmed) ? trimmed : null;
    }

    /// <summary>True when the value is a 64-character hex AES decryption key.</summary>
    public static bool IsValidAesKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        return Hex64Pattern.IsMatch(key.Trim());
    }

    /// <summary>
    /// Returns the top-level key names present in the document, in order.
    /// </summary>
    public static IReadOnlyList<string> TopLevelKeys(string content)
    {
        var keys = new List<string>();
        foreach (Match m in TopLevelKeyPattern.Matches(content))
        {
            keys.Add(m.Value.TrimEnd(':', ' ', '\t'));
        }
        return keys;
    }

    /// <summary>
    /// Finds duplicate top-level keys.
    ///
    /// This check exists because YAML does not: a parser resolves a repeated key to
    /// the final occurrence and reports no error, so a duplicated section silently
    /// replaces the original rather than failing loudly.
    /// </summary>
    public static IReadOnlyList<string> DuplicateTopLevelKeys(string content)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dupes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in TopLevelKeys(content))
        {
            if (!seen.Add(key)) dupes.Add(key);
        }

        return dupes.ToList();
    }

    /// <summary>
    /// Best-effort structural validation of the document.
    ///
    /// Deliberately conservative: a full YAML parse would reject things SLSsteam
    /// accepts, and false rejections would block legitimate edits. This catches the
    /// failures our text edits can actually cause - unbalanced flow collections and
    /// duplicate keys.
    /// </summary>
    public static bool ValidateContent(string content, out string reason)
    {
        reason = "";

        if (string.IsNullOrWhiteSpace(content))
        {
            reason = "content is empty";
            return false;
        }

        var dupes = DuplicateTopLevelKeys(content);
        if (dupes.Count > 0)
        {
            reason = $"duplicate top-level keys: {string.Join(", ", dupes)}";
            return false;
        }

        // Unbalanced brackets inside a single line mean a mangled flow collection.
        foreach (var line in content.Split('\n'))
        {
            var opens = line.Count(c => c == '[' || c == '{');
            var closes = line.Count(c => c == ']' || c == '}');
            if (opens != closes)
            {
                reason = $"unbalanced flow collection on line: {line.Trim()}";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Normalizes list indentation inside a section.
    ///
    /// SLSsteam's config is indented inconsistently in the wild, and a list item at
    /// column 0 under an indented block reads as a different structure to a strict
    /// parser. Entries are re-indented to two spaces, matching what SLSsteam writes.
    /// </summary>
    public static string FixListIndentation(string content, string sectionName)
    {
        var bounds = YamlSections.GetSectionBounds(content, sectionName);
        if (!bounds.HasValue) return content;

        var (_, contentStart, sectionEnd) = bounds.Value;
        var section = content.Substring(contentStart, sectionEnd - contentStart);

        // Match "- value" lines at any indentation, preserving any trailing comment.
        var fixedSection = Regex.Replace(
            section,
            @"^[ \t]*-[ \t]*([^\r\n#]+?)(?=[ \t]*(?:#|$))",
            "  - $1",
            RegexOptions.Multiline);

        if (fixedSection == section) return content;

        return content[..contentStart] + fixedSection + content[sectionEnd..];
    }

    /// <summary>
    /// Validates the document and, only if it holds up, commits it in place.
    ///
    /// The in-place write preserves the inode so SLSsteam's inotify watcher keeps
    /// working; a temp-file-plus-rename would silently detach it until restart.
    /// </summary>
    public static bool Commit(string path, string content, string operation)
    {
        if (!ValidateContent(content, out var reason))
        {
            // Never write content we could not verify: this file is read by SLSsteam
            // on startup and by ASSella, and a bad write is expensive to recover from.
            PlutoLogger.Error("Yaml", $"Refusing to write {path} after '{operation}': {reason}");
            return false;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(content);

            using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            fs.SetLength(0);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);

            return true;
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("Yaml", $"Failed writing {path} after '{operation}'", ex);
            return false;
        }
    }

    /// <summary>
    /// Creates a timestamp-free sibling backup before a destructive edit.
    /// Skips empty sources, since a 0-byte backup is worse than none.
    /// </summary>
    public static bool TryBackup(string path, string suffix = ".pluto.bak")
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (new FileInfo(path).Length == 0) return false;

            var backup = path + suffix;
            File.Copy(path, backup, overwrite: true);
            PlutoLogger.Info("Yaml", $"Backed up {path} to {backup}");
            return true;
        }
        catch (Exception ex)
        {
            PlutoLogger.Warn("Yaml", $"Could not back up {path}: {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// Section location helpers shared by the editor, mirroring ASSella's
/// <c>_get_section_bounds</c> / <c>_expand_flow_section_if_needed</c> pair.
/// </summary>
public static class YamlSections
{
    /// <summary>
    /// Returns (headerStart, contentStart, sectionEnd) for a top-level section,
    /// or null when the section is absent.
    ///
    /// <paramref name="sectionEnd"/> is the offset of the next top-level key, or
    /// end-of-file. Anything inserted at that offset lands after the section's last
    /// entry rather than under the following section's leading comments.
    /// </summary>
    public static (int HeaderStart, int ContentStart, int SectionEnd)? GetSectionBounds(string content, string sectionName)
    {
        // Accept a trailing flow collection ("Apps: []") so flow-style sections are
        // recognised rather than being duplicated by a later append.
        var headerPattern = new Regex(
            $@"^[ \t]*{Regex.Escape(sectionName)}[ \t]*:[ \t]*(?:\[[ \t]*\]|\{{[ \t]*\}})?[ \t]*(?:#[^\r\n]*)?$",
            RegexOptions.Multiline);

        var match = headerPattern.Match(content);
        if (!match.Success) return null;

        int headerStart = match.Index;
        int contentStart = match.Index + match.Length;

        if (contentStart < content.Length && content[contentStart] == '\r') contentStart++;
        if (contentStart < content.Length && content[contentStart] == '\n') contentStart++;

        var afterSection = content[contentStart..];
        var nextMatch = Regex.Match(afterSection, @"^[A-Za-z0-9_]+[ \t]*:", RegexOptions.Multiline);
        int sectionEnd = nextMatch.Success ? contentStart + nextMatch.Index : content.Length;

        return (headerStart, contentStart, sectionEnd);
    }

    /// <summary>
    /// Rewrites a flow-style header ("Apps: []") into block form ("Apps:").
    /// Without this, appending to the section produces a duplicate top-level key.
    /// </summary>
    public static string ExpandFlowSection(string content, string sectionName)
    {
        var bounds = GetSectionBounds(content, sectionName);
        if (!bounds.HasValue) return content;

        var (h, _, _) = bounds.Value;
        int lineEnd = content.IndexOf('\n', h);
        if (lineEnd < 0) lineEnd = content.Length;

        var headerLine = content[h..lineEnd];
        var flowMatch = Regex.Match(
            headerLine,
            $@"^([ \t]*{Regex.Escape(sectionName)}[ \t]*:)[ \t]*(?:\[[ \t]*\]|\{{[ \t]*\}})([ \t]*(?:#[^\r\n]*)?)$");

        if (!flowMatch.Success) return content;

        var rebuilt = flowMatch.Groups[1].Value + flowMatch.Groups[2].Value;
        return content[..h] + rebuilt + content[lineEnd..];
    }

    /// <summary>
    /// Finds where a new entry should be inserted: immediately after the section's
    /// last real list or map item. Inserting at the raw section end instead would
    /// place the entry below any trailing comment block belonging to the next
    /// section, which reparses as a comment on the wrong line.
    /// </summary>
    public static int FindInsertPosition(string content, (int HeaderStart, int ContentStart, int SectionEnd) bounds)
    {
        var (_, contentStart, sectionEnd) = bounds;

        // Scan by absolute index rather than accumulating offsets from a split.
        // Accumulating `line.Length + 1` drifts once a section contains a lone
        // "\r\n" terminator (Split yields "\r" but the newline is two characters),
        // which produced a negative insert position and threw on a 9KB section.
        int lastItemEnd = -1;
        int pos = contentStart;

        while (pos < sectionEnd)
        {
            int lineEnd = content.IndexOf('\n', pos);
            int lineStop = lineEnd < 0 ? content.Length : lineEnd + 1;
            if (lineStop > sectionEnd) lineStop = sectionEnd;

            var line = content[pos..lineStop];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                // Blank line: skip, do not treat as an item boundary.
            }
            else if (trimmed.StartsWith('#'))
            {
                // Comment: skip, so entries land above trailing comment blocks.
            }
            else if (line.StartsWith(' ') || line.StartsWith('\t'))
            {
                // An indented non-comment line is a real list or map entry.
                lastItemEnd = lineStop;
            }
            else
            {
                // Unindented, non-comment line ends the section body.
                break;
            }

            if (lineEnd < 0) break;
            pos = lineEnd + 1;
        }

        if (lastItemEnd >= 0) return Math.Clamp(lastItemEnd, contentStart, sectionEnd);
        return contentStart;
    }
}