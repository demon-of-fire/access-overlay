using System;
using System.IO;

namespace AccessOverlay.Core.Diagnostics;

public static class DiagnosticsLogger
{
    private static readonly object SyncLock = new();

    private static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AccessOverlay",
        "diagnostics.log");

    public static void Log(string message)
    {
        try
        {
            var path = LogFilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
            lock (SyncLock)
            {
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Logging must never crash the product.
        }
    }
}
