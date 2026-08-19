using System.Windows;
using AccessOverlay.Core.Settings;
using AccessOverlay.Core.Speech;

namespace AccessOverlay.App;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static SettingsStore SettingsStore { get; private set; } = new();
    public static SpeechRouter Speech { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SettingsStore = new SettingsStore();
        Settings = SettingsStore.Load();
        Speech = new SpeechRouter();
        Speech.RefreshStatus();

        // Keep a WPF host alive for the global hotkey.
        MainWindow = new MainWindow();
        // Launching the app shows the reader window (the default window);
        // automation mode (--web) stays quiet.
        if (!e.Args.Contains("--web"))
            ((MainWindow)MainWindow).ShowReaderAtStartup();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SettingsStore.Save(Settings);
        Speech.Dispose();
        base.OnExit(e);
    }
}
