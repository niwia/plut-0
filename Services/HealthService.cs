using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pluto.Services;

public class HubcapStats
{
    public bool IsConfigured { get; set; }
    public string Username { get; set; } = string.Empty;
    public int DailyUsage { get; set; }
    public int DailyLimit { get; set; } = 55;
    public int TotalCalls { get; set; }
    public bool CanMakeRequests { get; set; } = true;
    public string Expiration { get; set; } = string.Empty;
    public string StatusSummary => IsConfigured 
        ? $"{DailyUsage}/{DailyLimit}" 
        : "not configured";
}

public class SystemHealthStatus
{
    public bool SteamRunning { get; set; }
    public bool SlsBinaryDetected { get; set; }
    public string SlsBinaryPath { get; set; } = string.Empty;
    public bool SlsProcessActive { get; set; }
    public bool SlsConfigPresent { get; set; }
    public bool SlsFileWatcherDead { get; set; }
    public HubcapStats Hubcap { get; set; } = new();

    public bool IsOptimal => SteamRunning && SlsBinaryDetected && SlsProcessActive && SlsConfigPresent && !SlsFileWatcherDead;
    public string OverallState => IsOptimal ? "Good" : (SlsBinaryDetected ? "Attention" : "Offline");

    public List<string> Issues
    {
        get
        {
            var list = new List<string>();
            if (!SlsBinaryDetected) list.Add("SLSsteam binary not found");
            if (!SlsConfigPresent) list.Add("SLSsteam config.yaml missing");
            if (!SteamRunning) list.Add("Steam client is offline");
            else if (!SlsProcessActive) list.Add("SLSsteam not active in Steam");
            if (SlsFileWatcherDead) list.Add("SLSsteam file watcher crashed (restart Steam)");
            return list;
        }
    }
}

public class HealthService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly AccelaConfigService _configService;
    private HubcapStats? _cachedHubcapStats;
    private DateTime _lastHubcapFetch = DateTime.MinValue;
    private readonly SemaphoreSlim _hubcapLock = new(1, 1);

    public HealthService(AccelaConfigService? configService = null)
    {
        _configService = configService ?? new AccelaConfigService();
    }

    /// <summary>
    /// Evaluates current system, Steam, SLSsteam and API health.
    /// </summary>
    public async Task<SystemHealthStatus> CheckHealthAsync(bool refreshHubcap = false)
    {
        var status = new SystemHealthStatus();

        // 1. Steam Client Process
        try
        {
            status.SteamRunning = Process.GetProcessesByName("steam").Length > 0;
        }
        catch
        {
            status.SteamRunning = false;
        }

        // 2. SLSsteam Binary
        var (detected, binPath) = FindSlsBinary();
        status.SlsBinaryDetected = detected;
        status.SlsBinaryPath = binPath;

        // 3. SLS Config File
        var slsConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "SLSsteam", "config.yaml");
        status.SlsConfigPresent = File.Exists(slsConfigPath);

        // 4. SLS Process & Pipe Active
        const string pipePath = "/tmp/SLSsteam.API";
        bool pipeExists = File.Exists(pipePath);
        bool mapsActive = false;

        if (status.SteamRunning)
        {
            mapsActive = IsSlsMappedInSteam();
        }
        status.SlsProcessActive = pipeExists || mapsActive;

        // 5. SLS Filewatcher Crash Check
        status.SlsFileWatcherDead = CheckFileWatcherCrashed();

        // 6. Hubcap Stats
        status.Hubcap = await GetHubcapStatsAsync(refreshHubcap);

        return status;
    }

    private static (bool detected, string path) FindSlsBinary()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        {
            Path.Combine(home, ".local", "share", "Steam", "slssteam", "slssteam.so"),
            Path.Combine(home, ".local", "share", "Steam", "slssteam", "SLSsteam.so"),
            Path.Combine(home, ".config", "SLSsteam", "slssteam.so"),
            Path.Combine(home, ".config", "SLSsteam", "SLSsteam.so"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "slssteam", "slssteam.so"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam", "slssteam", "slssteam.so")
        };

        foreach (var p in candidates)
        {
            if (File.Exists(p)) return (true, p);
        }
        return (false, string.Empty);
    }

    private static bool IsSlsMappedInSteam()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("steam"))
            {
                var mapsPath = $"/proc/{p.Id}/maps";
                if (File.Exists(mapsPath))
                {
                    var lines = File.ReadLines(mapsPath);
                    foreach (var line in lines)
                    {
                        if (line.Contains("SLSsteam.so", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("slssteam.so", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch { }
        return false;
    }

    private static bool CheckFileWatcherCrashed()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var logPath = Path.Combine(home, ".SLSsteam.log");
            if (!File.Exists(logPath)) return false;

            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long seekOffset = Math.Max(0, fs.Length - 32768);
            fs.Seek(seekOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var tail = reader.ReadToEnd();
            return tail.Contains("Failed to read from FileWatcher", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task<HubcapStats> GetHubcapStatsAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedHubcapStats != null && (DateTime.UtcNow - _lastHubcapFetch).TotalSeconds < 30)
        {
            return _cachedHubcapStats;
        }

        await _hubcapLock.WaitAsync();
        try
        {
            if (!forceRefresh && _cachedHubcapStats != null && (DateTime.UtcNow - _lastHubcapFetch).TotalSeconds < 30)
            {
                return _cachedHubcapStats;
            }

            var apiKey = _configService.GetValue("morrenus_api_key", "").Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _cachedHubcapStats = new HubcapStats { IsConfigured = false };
                _lastHubcapFetch = DateTime.UtcNow;
                return _cachedHubcapStats;
            }

            var url = $"https://hubcapmanifest.com/api/v1/user/stats?api_key={Uri.EscapeDataString(apiKey)}";
            var resp = await HttpClient.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                _cachedHubcapStats = new HubcapStats { IsConfigured = true, Username = "offline/error" };
                _lastHubcapFetch = DateTime.UtcNow;
                return _cachedHubcapStats;
            }

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var stats = new HubcapStats
            {
                IsConfigured = true,
                Username = root.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "",
                DailyUsage = root.TryGetProperty("daily_usage", out var du) ? du.GetInt32() : 0,
                DailyLimit = root.TryGetProperty("daily_limit", out var dl) ? dl.GetInt32() : 55,
                TotalCalls = root.TryGetProperty("api_key_usage_count", out var tc) ? tc.GetInt32() : 0,
                CanMakeRequests = !root.TryGetProperty("can_make_requests", out var cm) || cm.GetBoolean(),
                Expiration = root.TryGetProperty("api_key_expires_at", out var ex) ? ex.GetString() ?? "" : ""
            };

            _cachedHubcapStats = stats;
            _lastHubcapFetch = DateTime.UtcNow;
            return stats;
        }
        catch (Exception ex)
        {
            PlutoLogger.Error("Health", "Failed to fetch Hubcap user stats", ex);
            return _cachedHubcapStats ?? new HubcapStats { IsConfigured = false };
        }
        finally
        {
            _hubcapLock.Release();
        }
    }

    // ── ASSfixer (SLS Config Fixer) Runner ───────────────────────────────────

    public static string FindSlsConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        {
            Path.Combine(home, ".config", "SLSsteam", "config.yaml"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".config", "SLSsteam", "config.yaml")
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return candidates[0];
    }

    /// <summary>
    /// Pure C# validation of SLSsteam config.yaml (syntax, duplicate keys, illegal tabs, line endings).
    /// </summary>
    public async Task<(bool success, string output)> RunAssfixerCheckAsync()
    {
        await Task.Yield();
        var configPath = FindSlsConfigPath();
        if (!File.Exists(configPath))
        {
            return (false, $"SLSsteam config.yaml not found at {configPath}");
        }

        try
        {
            var rawText = await File.ReadAllTextAsync(configPath);
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return (false, "config.yaml is empty.");
            }

            var issues = new List<string>();
            var lines = rawText.Split('\n');
            int gameCount = 0;
            string currentSection = string.Empty;
            var sectionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int duplicateCount = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                int lineno = i + 1;

                // Check for illegal tabs
                if (line.Contains('\t'))
                {
                    issues.Add($"Line {lineno}: contains illegal tab character (YAML requires spaces)");
                }

                // Check Windows CRLF
                if (line.EndsWith('\r'))
                {
                    issues.Add($"Line {lineno}: contains Windows CRLF line ending");
                }

                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#"))
                    continue;

                // Top level section
                if (!line.StartsWith(" ") && !line.StartsWith("\t") && trimmed.EndsWith(":"))
                {
                    currentSection = trimmed.TrimEnd(':').Trim();
                    sectionKeys.Clear();
                    continue;
                }

                // Child entry under list or map
                if (line.StartsWith("  ") && !line.StartsWith("    "))
                {
                    if (currentSection is "AppIds" or "AdditionalApps" or "FakeOffline")
                    {
                        if (trimmed.StartsWith("- "))
                        {
                            var val = trimmed[2..].Split('#')[0].Trim();
                            if (!sectionKeys.Add(val))
                            {
                                duplicateCount++;
                                issues.Add($"Duplicate entry '{val}' in {currentSection} (Line {lineno})");
                            }
                            else if (currentSection == "AppIds" || currentSection == "AdditionalApps")
                            {
                                gameCount++;
                            }
                        }
                    }
                    else if (currentSection is "AppTokens" or "FakeAppIds" or "GameTitles" or "SubscriptionTimestamps")
                    {
                        var colonIdx = trimmed.IndexOf(':');
                        if (colonIdx > 0)
                        {
                            var key = trimmed[..colonIdx].Trim();
                            if (!sectionKeys.Add(key))
                            {
                                duplicateCount++;
                                issues.Add($"Duplicate mapping key '{key}' in {currentSection} (Line {lineno})");
                            }
                            else if (currentSection == "AppTokens")
                            {
                                gameCount++;
                            }
                        }
                    }
                }
            }

            if (issues.Count > 0)
            {
                var summary = string.Join("\n• ", issues.Take(6));
                if (issues.Count > 6) summary += $"\n... and {issues.Count - 6} more issue(s)";
                return (false, $"Found {issues.Count} issue(s):\n• {summary}");
            }

            return (true, $"Config healthy: {gameCount} game entries verified, clean YAML indentation, 0 duplicate keys.");
        }
        catch (Exception ex)
        {
            return (false, $"Error validating config: {ex.Message}");
        }
    }

    /// <summary>
    /// Pure C# repair of SLSsteam config.yaml: creates timestamped backup, normalizes indentation & line endings,
    /// deduplicates keys in sections, and cleans trailing whitespace.
    /// </summary>
    public async Task<(bool success, string output)> RunAssfixerRepairAsync()
    {
        await Task.Yield();
        var configPath = FindSlsConfigPath();
        if (!File.Exists(configPath))
        {
            return (false, $"SLSsteam config.yaml not found at {configPath}");
        }

        try
        {
            var dir = Path.GetDirectoryName(configPath) ?? string.Empty;
            var backupName = $"config.yaml.{DateTime.UtcNow:yyyyMMdd_HHmmss}.bak";
            var backupPath = Path.Combine(dir, backupName);
            File.Copy(configPath, backupPath, true);

            var rawText = await File.ReadAllTextAsync(configPath);
            var lines = rawText.Replace("\r\n", "\n").Split('\n');
            var repairedLines = new List<string>();

            string currentSection = string.Empty;
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int fixesCount = 0;

            foreach (var line in lines)
            {
                // Convert tabs to 2 spaces
                string processed = line.Replace("\t", "  ");
                if (processed != line) fixesCount++;

                // Strip trailing spaces
                string rtrimmed = processed.TrimEnd();
                if (rtrimmed != processed) fixesCount++;

                string trimmed = rtrimmed.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#"))
                {
                    repairedLines.Add(rtrimmed);
                    continue;
                }

                // Top level section
                if (!rtrimmed.StartsWith(" ") && trimmed.EndsWith(":"))
                {
                    currentSection = trimmed.TrimEnd(':').Trim();
                    seenKeys.Clear();
                    repairedLines.Add(rtrimmed);
                    continue;
                }

                // Map or list entry under section
                if (rtrimmed.StartsWith("  ") && !rtrimmed.StartsWith("    "))
                {
                    if (currentSection is "AppIds" or "AdditionalApps" or "FakeOffline" && trimmed.StartsWith("- "))
                    {
                        var val = trimmed[2..].Split('#')[0].Trim();
                        if (!seenKeys.Add(val))
                        {
                            // Deduplicate duplicate list item
                            fixesCount++;
                            continue;
                        }
                    }
                    else if (currentSection is "AppTokens" or "FakeAppIds" or "GameTitles" or "SubscriptionTimestamps")
                    {
                        var colonIdx = trimmed.IndexOf(':');
                        if (colonIdx > 0)
                        {
                            var key = trimmed[..colonIdx].Trim();
                            if (!seenKeys.Add(key))
                            {
                                // Deduplicate duplicate mapping key
                                fixesCount++;
                                continue;
                            }
                        }
                    }
                }

                repairedLines.Add(rtrimmed);
            }

            var cleanOutput = string.Join("\n", repairedLines) + "\n";
            await File.WriteAllTextAsync(configPath, cleanOutput);
            File.SetLastWriteTimeUtc(configPath, DateTime.UtcNow);

            PlutoLogger.Info("Health", $"Repaired config.yaml: {fixesCount} fixes applied. Backup: {backupName}");
            return (true, $"Repaired: {fixesCount} formatting/duplicate fixes applied. Backup created: {backupName}");
        }
        catch (Exception ex)
        {
            return (false, $"Error repairing config: {ex.Message}");
        }
    }

    public bool HasAssfixerBackup()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, ".config", "SLSsteam");
        if (!Directory.Exists(dir)) return false;
        return Directory.GetFiles(dir, "config.yaml*.bak").Length > 0 || File.Exists(Path.Combine(dir, "config.yaml.bak"));
    }

    public (bool success, string message) RestoreAssfixerBackup()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, ".config", "SLSsteam");
        var target = Path.Combine(dir, "config.yaml");

        try
        {
            if (!Directory.Exists(dir)) return (false, "SLSsteam config folder not found.");

            var backups = Directory.GetFiles(dir, "config.yaml*.bak")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .ToList();

            if (backups.Count == 0) return (false, "No backup files found.");

            var latest = backups[0];
            File.Copy(latest, target, true);
            PlutoLogger.Info("Health", $"Restored config backup from {latest}");
            return (true, $"Restored from {Path.GetFileName(latest)}");
        }
        catch (Exception ex)
        {
            return (false, $"Restore error: {ex.Message}");
        }
    }

    // ── Sanitation Operations ────────────────────────────────────────────────

    public (int filesDeleted, double mbFreed) ClearThumbnailCache()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cacheDir = Path.Combine(home, ".local", "share", "ACCELA", "image_cache");
        if (!Directory.Exists(cacheDir)) return (0, 0);

        int count = 0;
        long bytes = 0;

        try
        {
            var files = Directory.GetFiles(cacheDir);
            foreach (var f in files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    bytes += fi.Length;
                    File.Delete(f);
                    count++;
                }
                catch { }
            }
        }
        catch { }

        return (count, bytes / (1024.0 * 1024.0));
    }

    public bool TouchSlsConfig()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(home, ".config", "SLSsteam", "config.yaml");
        if (!File.Exists(path)) return false;

        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            PlutoLogger.Info("Health", "Touched config.yaml to trigger SLSsteam inotify reload.");
            return true;
        }
        catch { return false; }
    }
}
