using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Pluto.Services;

/// <summary>
/// Native C# parser and manager for ~/.config/Tachibana Labs/ACCELA.conf.
/// Preserves Qt @Variant entries and custom sections while cleanly reading/writing [General] keys.
/// </summary>
public class AccelaConfigService
{
    public static readonly string DefaultConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "Tachibana Labs", "ACCELA.conf");

    private readonly string _configPath;
    private readonly object _lock = new();

    public AccelaConfigService(string? configPath = null)
    {
        _configPath = configPath ?? DefaultConfigPath;
    }

    public bool Exists => File.Exists(_configPath);

    /// <summary>
    /// Reads a string setting from the [General] section of ACCELA.conf.
    /// </summary>
    public string GetValue(string key, string defaultValue = "")
    {
        lock (_lock)
        {
            if (!File.Exists(_configPath)) return defaultValue;

            try
            {
                var lines = File.ReadAllLines(_configPath);
                bool inGeneral = false;

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        inGeneral = trimmed.Equals("[General]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (inGeneral)
                    {
                        var match = Regex.Match(line, @"^\s*([a-zA-Z0-9_\-]+)\s*=\s*(.*)$");
                        if (match.Success && match.Groups[1].Value.Equals(key, StringComparison.OrdinalIgnoreCase))
                        {
                            return match.Groups[2].Value.Trim().Trim('"', '\'');
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PlutoLogger.Error("Config", $"Error reading setting {key} from {_configPath}", ex);
            }

            return defaultValue;
        }
    }

    /// <summary>
    /// Reads a boolean setting from the [General] section of ACCELA.conf.
    /// </summary>
    public bool GetBool(string key, bool defaultValue = false)
    {
        var val = GetValue(key, defaultValue ? "true" : "false").ToLowerInvariant();
        return val is "true" or "1" or "yes" or "on";
    }

    /// <summary>
    /// Reads an integer setting from the [General] section of ACCELA.conf.
    /// </summary>
    public int GetInt(string key, int defaultValue = 0)
    {
        var val = GetValue(key, defaultValue.ToString());
        return int.TryParse(val, out int res) ? res : defaultValue;
    }

    /// <summary>
    /// Writes an integer setting into the [General] section.
    /// </summary>
    public bool SetInt(string key, int value)
    {
        return SetValue(key, value.ToString());
    }

    /// <summary>
    /// Writes a setting into the [General] section of ACCELA.conf safely and in-place.
    /// Preserves all other sections, comments, and Qt @Variant binary strings untouched.
    /// </summary>
    public bool SetValue(string key, string value)
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var lines = File.Exists(_configPath) ? new List<string>(File.ReadAllLines(_configPath)) : new List<string>();

                int generalHeaderIndex = -1;
                int nextSectionIndex = -1;
                int existingKeyIndex = -1;

                for (int i = 0; i < lines.Count; i++)
                {
                    var trimmed = lines[i].Trim();
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        if (trimmed.Equals("[General]", StringComparison.OrdinalIgnoreCase))
                        {
                            generalHeaderIndex = i;
                        }
                        else if (generalHeaderIndex != -1 && nextSectionIndex == -1)
                        {
                            nextSectionIndex = i;
                        }
                        continue;
                    }

                    if (generalHeaderIndex != -1 && nextSectionIndex == -1)
                    {
                        var match = Regex.Match(lines[i], @"^\s*([a-zA-Z0-9_\-]+)\s*=");
                        if (match.Success && match.Groups[1].Value.Equals(key, StringComparison.OrdinalIgnoreCase))
                        {
                            existingKeyIndex = i;
                            break;
                        }
                    }
                }

                string newLine = $"{key} = {value}";

                if (existingKeyIndex != -1)
                {
                    lines[existingKeyIndex] = newLine;
                }
                else if (generalHeaderIndex != -1)
                {
                    // Insert right after [General]
                    lines.Insert(generalHeaderIndex + 1, newLine);
                }
                else
                {
                    // No [General] section at all, add at top
                    lines.Insert(0, "[General]");
                    lines.Insert(1, newLine);
                }

                var tempPath = _configPath + ".tmp";
                File.WriteAllLines(tempPath, lines);
                File.Move(tempPath, _configPath, overwrite: true);

                PlutoLogger.Info("Config", $"Updated setting {key} = {value}");
                return true;
            }
            catch (Exception ex)
            {
                PlutoLogger.Error("Config", $"Failed to write setting {key} to {_configPath}", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Writes a boolean setting into the [General] section.
    /// </summary>
    public bool SetBool(string key, bool value)
    {
        return SetValue(key, value ? "true" : "false");
    }

    /// <summary>
    /// Loads all settings in [General] into a dictionary.
    /// </summary>
    public Dictionary<string, string> GetAllGeneralSettings()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            if (!File.Exists(_configPath)) return result;

            try
            {
                var lines = File.ReadAllLines(_configPath);
                bool inGeneral = false;

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        inGeneral = trimmed.Equals("[General]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (inGeneral)
                    {
                        var match = Regex.Match(line, @"^\s*([a-zA-Z0-9_\-]+)\s*=\s*(.*)$");
                        if (match.Success)
                        {
                            result[match.Groups[1].Value] = match.Groups[2].Value.Trim().Trim('"', '\'');
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PlutoLogger.Error("Config", $"Error loading general settings from {_configPath}", ex);
            }

            return result;
        }
    }
}
