using System;
using System.IO;

namespace Pluto.Services;

public static class PlutoLogger
{
    private static readonly object _lock = new();

    public static readonly string LogDirectory = PlutoPaths.Logs;

    public static readonly string LogFilePath = Path.Combine(LogDirectory, "pluto.log");

    static PlutoLogger()
    {
        // Nothing useful to do if the log dir can't be made - logging must never throw.
        PlutoPaths.EnsureDirectory(LogDirectory);
    }

    public static void Info(string category, string message) => Log("INFO", category, message);

    public static void Warn(string category, string message) => Log("WARN", category, message);

    public static void Error(string category, string message, Exception? ex = null)
    {
        var msg = ex != null ? $"{message} (Exception: {ex.Message})" : message;
        Log("ERROR", category, msg);
    }

    private static void Log(string level, string category, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{timestamp}] [{level}] [{category}] {message}";

        // Write to stdout
        Console.WriteLine(line);

        // Write to log file
        lock (_lock)
        {
            try
            {
                PlutoPaths.EnsureDirectory(LogDirectory);

                // Rotate if > 5MB
                if (File.Exists(LogFilePath) && new FileInfo(LogFilePath).Length > 5 * 1024 * 1024)
                {
                    var oldPath = LogFilePath + ".old";
                    if (File.Exists(oldPath)) File.Delete(oldPath);
                    File.Move(LogFilePath, oldPath);
                }

                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
            catch
            {
                // Deliberately silent: a failed log write must not take down the caller.
                // The Console.WriteLine above already emitted the line.
            }
        }
    }
}
