using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Pluto.Models;

namespace Pluto.Services;

/// <summary>
/// Bridges Pluto with the Python backend scripts in Pluto/Scripts.
/// Inherits yaml_config_manager, plugin_games, sls_bridge, and ACCELA.conf settings.
/// </summary>
public class At0mScriptBridge
{
    private static readonly string ProjectScriptDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Documents", "pluto", "Scripts");

    private static readonly string AppScriptDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Scripts");

    private string ScriptPath
    {
        get
        {
            var p1 = Path.Combine(AppScriptDir, "pluto_sls.py");
            if (File.Exists(p1)) return p1;
            var p2 = Path.Combine(ProjectScriptDir, "pluto_sls.py");
            if (File.Exists(p2)) return p2;
            return "pluto_sls.py";
        }
    }

    public async Task<string> RunCommandAsync(string command, params string[] args)
    {
        var script = ScriptPath;
        var arguments = $"\"{script}\" {command} " + string.Join(" ", args);

        var psi = new ProcessStartInfo
        {
            FileName = "python3",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return string.Empty;

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                Console.WriteLine($"[At0mScriptBridge] Error running {command}: {stderr}");
            }

            return stdout.Trim();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[At0mScriptBridge] Exception running {command}: {ex.Message}");
            return string.Empty;
        }
    }

    public async Task<List<PluginGame>> ListAllGamesAsync()
    {
        var output = await RunCommandAsync("list-all");
        if (string.IsNullOrWhiteSpace(output)) return new List<PluginGame>();

        try
        {
            var games = JsonSerializer.Deserialize<List<PluginGame>>(output, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return games ?? new List<PluginGame>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[At0mScriptBridge] Failed to parse list-all: {ex.Message}");
            return new List<PluginGame>();
        }
    }

    public async Task<bool> ConvertToAtomAsync(string appId, string? installPath = null)
    {
        var args = string.IsNullOrEmpty(installPath) ? new[] { appId } : new[] { appId, "--install-path", $"\"{installPath}\"" };
        var output = await RunCommandAsync("to-atom", args);
        return output.Contains("\"success\": true");
    }

    public async Task<bool> ConvertToAccelaAsync(string appId, string? installPath = null)
    {
        var args = string.IsNullOrEmpty(installPath) ? new[] { appId } : new[] { appId, "--install-path", $"\"{installPath}\"" };
        var output = await RunCommandAsync("to-accela", args);
        return output.Contains("\"success\": true");
    }

    public async Task<bool> SyncYamlAsync(string appId)
    {
        var output = await RunCommandAsync("sync-yaml", appId);
        return output.Contains("\"success\": true");
    }

    public async Task<bool> ReloadSlsAsync()
    {
        var output = await RunCommandAsync("reload");
        return output.Contains("\"success\": true");
    }

    public async Task<Dictionary<string, string>> GetSettingsAsync()
    {
        var output = await RunCommandAsync("get-settings");
        if (string.IsNullOrWhiteSpace(output)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(output) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public async Task<bool> SetSettingAsync(string key, string value)
    {
        var output = await RunCommandAsync("set-setting", key, value);
        return output.Contains("\"success\": true");
    }
}
