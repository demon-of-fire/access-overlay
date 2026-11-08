using System;
using System.IO;

namespace AccessOverlay.Core.Diagnostics;

public static class DiagnosticsLogger
{
    private const long MaxLogBytes = 256 * 1024;
    private static readonly object SyncLock = new();

    /// <summary>Debug mode, toggled with F7. Nothing is logged unless it is on, and it resets each launch.</summary>
    public static bool Enabled { get; set; }

    private static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessOverlay",
        "diagnostics.log");

    public static void Log(string message)
    {
        if (!Enabled) return;
        try
        {
            var path = LogFilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
            lock (SyncLock)
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
                    File.Delete(path);
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Logging must never crash the product.
        }
    }
}
