using System.IO;
using System.Text.Json;

namespace AccessOverlay.Core.Settings;

public sealed class AppSettings
{
    public double FontSize { get; set; } = 20;
    public string Theme { get; set; } = "HighContrast";
    public double Opacity { get; set; } = 0.92;
    public bool AutoFollow { get; set; } = false;
    public bool SpeakOnCapture { get; set; } = true;
    public string Hotkey { get; set; } = "Ctrl+Alt+A";
    public double WindowWidth { get; set; } = 760;
    public double WindowHeight { get; set; } = 640;
}

/// <summary>JSON settings persisted under %APPDATA%\AccessOverlay\settings.json.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AccessOverlay", "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            lock (_lock)
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            lock (_lock)
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
            }
        }
        catch
        {
            // Settings persistence must never crash the app.
        }
    }
}
