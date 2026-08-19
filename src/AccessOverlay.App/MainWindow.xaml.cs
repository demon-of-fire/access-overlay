using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using AccessOverlay.App.Hotkey;
using AccessOverlay.App.Native;
using AccessOverlay.App.Overlay;
using AccessOverlay.App.Theming;
using AccessOverlay.Core.AppCapture;
using AccessOverlay.Core.Diagnostics;
using AccessOverlay.Core.Model;
using AccessOverlay.Core.Normalization;
using AccessOverlay.Core.Settings;
using AccessOverlay.Core.Speech;

namespace AccessOverlay.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly HotkeyManager? _hotkey;
    private readonly HotkeyManager? _gameHotkey;
    private ForegroundWatcher? _watcher;
    private OverlayWindow? _overlay;
    private OverlayWindow? _gameOverlay;
    private ReadableDocument? _lastDoc;
    private IntPtr _lastSourceHwnd;
    private bool _lastSourceIsWeb;
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;
    private bool _captureInFlight;
    private bool _gameScanInFlight;
    private DateTime _lastAutoCapture = DateTime.MinValue;
    private bool _automationMode;
    private bool _pressExecuted;
    private bool _suppressAutoFollowPersistence;
    private ReadableDocument? _lastGameDoc;
    private System.Windows.Threading.DispatcherTimer? _gameAutoReadTimer;

    public MainWindow()
    {
        InitializeComponent();
        // Never steal focus at launch: the overlay must follow whatever the user
        // was already working in, or there is nothing to follow.
        ShowActivated = false;
        _settings = App.Settings;
        _store = App.SettingsStore;

        ThemeManager.Apply(this, _settings.Theme);

        FontSlider.Value = _settings.FontSize;
        FontValueText.Text = _settings.FontSize.ToString("0");
        OpacitySlider.Value = _settings.Opacity;
        ThemeCombo.ItemsSource = ThemeManager.Names;
        ThemeCombo.SelectedItem = _settings.Theme;
        // Explicit hotkey mode: never capture or show a reader at startup.
        _settings.AutoFollow = false;
        AutoFollowCheck.IsChecked = false;
        SpeakOnCaptureCheck.IsChecked = _settings.SpeakOnCapture;

        _hotkey = new HotkeyManager(this, _settings.Hotkey);
        if (_hotkey.IsRegistered)
            _hotkey.HotkeyPressed += HotkeyPressed;
        else
            StatusText.Text = $"Hotkey {_settings.Hotkey} could not be registered (in use elsewhere?).";

        // Ctrl+Alt+G toggles the game window: scans the foreground game with OCR
        // when shown, and just hides it when it is already visible.
        _gameHotkey = new HotkeyManager(this, "Ctrl+Alt+G", id: 0x0A10);
        if (_gameHotkey.IsRegistered)
            _gameHotkey.HotkeyPressed += GameHotkeyPressed;
        else
            StatusText.Text += " Ctrl+Alt+G could not be registered (in use elsewhere?).";

        // This hidden host now waits for the global hotkey.
        _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) => UpdateNvdaStatus();
        _statusTimer.Start();
        UpdateNvdaStatus();

        App.Speech.OutputChanged += name => NvdaStatusText.Text = $"Reading via {name}";

        _ = InitWebViewAsync();
    }

    public void ShowControlPanel()
    {
        Show();
        Activate();
    }

    /// <summary>
    /// The reader window is the default window. It captures whatever the user was
    /// working in, then appears with that content (or empty if nothing to read).
    /// </summary>
    public void ShowReaderAtStartup()
    {
        EnsureOverlay(new ReadableDocument());
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd != IntPtr.Zero && GetPid(hwnd) != Environment.ProcessId && !IsShellOrDesktop(hwnd))
        {
            Log($"startup capturing fg=0x{hwnd.ToInt64():X} '{NativeMethods.GetWindowTextSafe(hwnd)}'");
            CaptureWindow(hwnd, auto: true);
        }
        _overlay!.ShowOverlayNoActivate();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();
            var webArg = Environment.GetCommandLineArgs().SkipWhile(a => a != "--web").Skip(1).FirstOrDefault();
            if (!string.IsNullOrEmpty(webArg))
            {
                // Debug/automation mode: --web <url> preloads a page and reads it automatically.
                _automationMode = true;
                _suppressAutoFollowPersistence = true;
                UrlBox.Text = webArg;
                WebReadAfterLoadCheck.IsChecked = true;
                AutoFollowCheck.IsChecked = false; // keep foreground auto-follow out of the way during automation
                StopWatcher();
                WebView.CoreWebView2.Navigate(NormalizeUrl(webArg));
            }
        }
        catch
        {
            StatusText.Text = "WebView2 runtime unavailable; desktop app capture still works.";
        }
    }

    private static void Log(string message)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "accessoverlay-diag.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch { }
    }

    private void UpdateNvdaStatus()
    {
        var ok = App.Speech.NvdaAvailable;
        NvdaStatusText.Text = ok
            ? $"NVDA: connected ({App.Speech.CurrentOutput})"
            : "NVDA: not running â€” using Windows SAPI";
    }

    private void StartWatcher()
    {
        try
        {
            _watcher ??= new ForegroundWatcher(TimeSpan.FromMilliseconds(350));
            _watcher.ForegroundChanged += OnForegroundChanged;
        }
        catch
        {
            StatusText.Text = "Foreground watching unavailable.";
        }
    }

    private void StopWatcher()
    {
        if (_watcher is null) return;
        _watcher.ForegroundChanged -= OnForegroundChanged;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnForegroundChanged(IntPtr hwnd)
    {
        DiagnosticsLogger.Log($"Foreground changed hwnd=0x{hwnd.ToInt64():X} title='{NativeMethods.GetWindowTextSafe(hwnd)}'");
        Log($"fg-change 0x{hwnd.ToInt64():X} '{NativeMethods.GetWindowTextSafe(hwnd)}'");
        if (hwnd == IntPtr.Zero) return;
        var pid = GetPid(hwnd);
        if (pid == Environment.ProcessId) return;
        if (IsShellOrDesktop(hwnd)) return;
        if (DateTime.UtcNow - _lastAutoCapture < TimeSpan.FromMilliseconds(900)) return;
        _lastAutoCapture = DateTime.UtcNow;
        CaptureWindow(hwnd, auto: true);
    }

    private static bool IsShellOrDesktop(IntPtr hwnd)
    {
        var cls = NativeMethods.GetWindowClassName(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "TaskListThumbnailWnd") return true;
        var title = NativeMethods.GetWindowTextSafe(hwnd);
        return title.Length == 0 || title.Equals("Program Manager", StringComparison.OrdinalIgnoreCase);
    }

    private static uint GetPid(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    private void HotkeyPressed()
    {
        DiagnosticsLogger.Log("Global hotkey pressed");
        // Ctrl+Alt+A toggles the main reader window. Hidden → capture the focused
        // app and show it; visible → hide it.
        if (_overlay?.IsVisible == true)
        {
            _overlay.Hide();
            return;
        }

        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || GetPid(hwnd) == Environment.ProcessId)
        {
            StatusText.Text = "Select another app, then press the hotkey again.";
            return;
        }

        CaptureWindow(hwnd, auto: false);
    }

    private void GameHotkeyPressed()
    {
        DiagnosticsLogger.Log("Game hotkey pressed");
        // Ctrl+Alt+G toggles the separate game window. Hidden → OCR-scan the
        // focused game and show it; visible → hide it.
        if (_gameOverlay?.IsVisible == true)
        {
            _gameOverlay.Hide();
            return;
        }

        GameScanAsync();
    }

    private void CaptureBtn_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        CaptureWindow(hwnd, auto: false);
    }

    private void CaptureWindow(IntPtr hwnd, bool auto)
    {
        if (hwnd == IntPtr.Zero || _captureInFlight) return;
        _captureInFlight = true;
        StatusText.Text = "Capturing…";
        DiagnosticsLogger.Log($"CaptureWindow started hwnd=0x{hwnd.ToInt64():X} auto={auto}");

        var sourceLabel = auto ? "Foreground app" : "Foreground app";
        Task.Run(() =>
        {
            try
            {
                var raw = UiaCaptureService.CaptureWindow(hwnd);
                var title = UiaCaptureService.GetWindowTitle(hwnd);
                var doc = DocumentNormalizer.Normalize(raw, title: title, source: sourceLabel);
                return (Ok: true, Doc: doc, Title: title);
            }
            catch (Exception ex)
            {
                return (Ok: false, Doc: (ReadableDocument?)null, Title: ex.Message);
            }
        }).ContinueWith(t =>
        {
            _captureInFlight = false;
            var r = t.Result;
            if (!r.Ok || r.Doc is null)
            {
                StatusText.Text = $"Capture failed: {r.Title}";
                return;
            }

            _lastDoc = r.Doc;
            _lastSourceHwnd = hwnd;
            _lastSourceIsWeb = false;
            LastSourceText.Text = r.Title;
            LastSummaryText.Text = $"{r.Doc.Blocks.Count} blocks Â· {r.Doc.PlainText.Length:N0} characters";

            if (_overlay is not null && _overlay.IsVisible)
            {
                // Follow the app the user moved to: update silently, don't steal focus.
                _overlay.UpdateDocument(r.Doc);
            }
            else if (auto)
            {
                // The overlay runs on its own: first capture opens it without stealing focus.
                var ov = EnsureOverlay(r.Doc);
                ov.ShowOverlayNoActivate();
                if (_settings.SpeakOnCapture)
                    AnnounceNowShowing(r.Doc);
            }
            else
            {
                var ov = EnsureOverlay(r.Doc);
                PositionNear(ov, hwnd);
                if (_settings.SpeakOnCapture && !string.IsNullOrWhiteSpace(r.Doc.PlainText))
                {
                    App.Speech.Speak($"{r.Doc.Title}. {r.Doc.PlainText}");
                }
                ov.ShowAndFocusFirst();
            }
            StatusText.Text = $"Captured: {r.Title}";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static void AnnounceNowShowing(ReadableDocument doc)
    {
        var count = doc.Blocks.Count(b => b is ControlBlock);
        var line = string.IsNullOrWhiteSpace(doc.Title)
            ? "Access overlay is following this window."
            : $"Now showing {doc.Title}. {count} buttons and links available. Tab into the reader to move through them.";
        App.Speech.Speak(line);
    }

    private OverlayWindow EnsureOverlay(ReadableDocument doc)
    {
        if (_overlay is null)
        {
            _overlay = new OverlayWindow(doc, App.Speech);
            _overlay.ControlActivated += OnControlActivated;
            _overlay.RefreshRequested += OnRefreshRequested;
            _overlay.ControlPanelRequested += (_, _) => ShowControlPanel();
        }
        else
        {
            _overlay.UpdateDocument(doc);
        }
        return _overlay;
    }

    /// <summary>
    /// The game scan has its own window, separate from the app/website reader.
    /// Ctrl+Alt+G shows and hides it; auto-read polls update it silently.
    /// </summary>
    private OverlayWindow EnsureGameOverlay(ReadableDocument doc)
    {
        if (_gameOverlay is null)
        {
            _gameOverlay = new OverlayWindow(doc, App.Speech);
            _gameOverlay.ControlActivated += (_, _) => GameScanAsync(silent: true);
            _gameOverlay.RefreshRequested += (_, _) => GameScanAsync(silent: true);
            _gameOverlay.ControlPanelRequested += (_, _) => ShowControlPanel();
        }
        else
        {
            _gameOverlay.UpdateDocument(doc);
        }
        return _gameOverlay;
    }

    /// <summary>
    /// Something was just pressed in the overlay. Re-read the same source so the
    /// overlay reflects the new state of the page or app.
    /// </summary>
    private async void OnControlActivated(object? sender, EventArgs e)
    {
        DiagnosticsLogger.Log($"Control activated, refreshing source hwnd=0x{_lastSourceHwnd.ToInt64():X} web={_lastSourceIsWeb}");
        await Task.Delay(350);
        if (_lastSourceIsWeb)
            ReadCurrentWebPage(silent: true);
        else if (_lastSourceHwnd != IntPtr.Zero)
            CaptureWindow(_lastSourceHwnd, auto: true);
    }

    /// <summary>Re-captures the page or application currently shown in the reader.</summary>
    private void OnRefreshRequested(object? sender, EventArgs e)
    {
        DiagnosticsLogger.Log($"Refresh requested, source hwnd=0x{_lastSourceHwnd.ToInt64():X} web={_lastSourceIsWeb}");
        if (_lastSourceIsWeb)
        {
            ReadCurrentWebPage(silent: true);
            return;
        }

        if (_lastSourceHwnd != IntPtr.Zero)
        {
            CaptureWindow(_lastSourceHwnd, auto: true);
            return;
        }

        StatusText.Text = "Nothing has been captured yet.";
    }

    private void PositionNear(OverlayWindow overlay, IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        var work = SystemParameters.WorkArea;
        var w = Math.Min(_settings.WindowWidth, work.Width * 0.92);
        var h = Math.Min(_settings.WindowHeight, work.Height * 0.92);
        double left = rect.Right - 40;
        if (left + w > work.Right) left = Math.Max(work.Left, rect.Left + 40 - w);
        var top = Math.Max(work.Top, rect.Top + 30);
        if (top + h > work.Bottom) top = Math.Max(work.Top, work.Bottom - h - 20);
        overlay.Left = Math.Max(work.Left, left);
        overlay.Top = top;
        overlay.Width = w;
        overlay.Height = h;
    }

    private void OpenOverlayBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDoc is null)
        {
            StatusText.Text = "Nothing captured yet â€” press Ctrl+Alt+A or click Capture.";
            return;
        }
        EnsureOverlay(_lastDoc).ShowAndFocusFirst();
    }

    private void CopyTextBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDoc is null) return;
        try
        {
            Clipboard.SetText(_lastDoc.PlainText);
            StatusText.Text = "Copied to clipboard.";
        }
        catch
        {
            StatusText.Text = "Clipboard busy, try again.";
        }
    }

    private void TestSpeech_Click(object sender, RoutedEventArgs e)
    {
        App.Speech.Speak("Access overlay connected. This text is being read through the fixed, readable pipeline.");
        UpdateNvdaStatus();
    }

    private void FontSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settings is null || FontValueText is null) return; // firing during InitializeComponent
        _settings.FontSize = Math.Round(FontSlider.Value);
        FontValueText.Text = _settings.FontSize.ToString("0");
        if (_overlay is not null) _overlay.Blocks.FontSize = _settings.FontSize;
        _store.Save(_settings);
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settings is null || OpacitySlider is null) return; // firing during InitializeComponent
        _settings.Opacity = Math.Round(OpacitySlider.Value, 2);
        if (_overlay is not null) _overlay.Opacity = _settings.Opacity;
        _store.Save(_settings);
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedItem is not string theme) return;
        _settings.Theme = theme;
        ThemeManager.Apply(this, theme);
        if (_overlay is not null) ThemeManager.Apply(_overlay, theme);
        _store.Save(_settings);
    }

    private void AutoFollow_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoFollowPersistence) return;
        _settings.AutoFollow = AutoFollowCheck.IsChecked == true;
        if (_settings.AutoFollow) StartWatcher();
        else StopWatcher();
        _store.Save(_settings);
    }

    private void SpeakOnCapture_Changed(object sender, RoutedEventArgs e)
    {
        _settings.SpeakOnCapture = SpeakOnCaptureCheck.IsChecked == true;
        _store.Save(_settings);
    }

    private async void WebGo_Click(object sender, RoutedEventArgs e)
    {
        await NavigateToUrl(UrlBox.Text);
    }

    private async void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await NavigateToUrl(UrlBox.Text);
        }
    }

    private static string NormalizeUrl(string raw)
    {
        var url = raw.Trim();
        if (url.Length == 0) return "";
        if (!url.Contains("://"))
            url = "https://" + url;
        return url;
    }

    private async Task NavigateToUrl(string raw)
    {
        var url = NormalizeUrl(raw);
        if (url.Length == 0) return;
        StatusText.Text = $"Loading {url}â€¦";
        try
        {
            await WebView.EnsureCoreWebView2Async();
            WebView.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Web view error: {ex.Message}";
        }
    }

    private void WebView_NavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        StatusText.Text = "Page loaded â€” extracting readable viewâ€¦";
        if (WebReadAfterLoadCheck.IsChecked == true)
        {
            // Give the page a moment to finish rendering its accessibility tree.
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                ReadCurrentWebPage();
            };
            timer.Start();
        }
    }

    private void WebRead_Click(object sender, RoutedEventArgs e)
    {
        ReadCurrentWebPage();
    }

    private async void ReadCurrentWebPage(bool silent = false)
    {
        if (WebView.CoreWebView2 is null)
        {
            StatusText.Text = "Open a page first.";
            return;
        }
        DiagnosticsLogger.Log($"ReadCurrentWebPage started url={UrlBox.Text}");
        var hostHwnd = new WindowInteropHelper(this).Handle;
        var webHwnd = NativeMethods.FindWebView2Hwnd(hostHwnd);
        if (webHwnd == IntPtr.Zero)
        {
            for (int retry = 0; retry < 6 && webHwnd == IntPtr.Zero; retry++)
            {
                await Task.Delay(250);
                webHwnd = NativeMethods.FindWebView2Hwnd(hostHwnd);
            }
        }

        if (webHwnd == IntPtr.Zero)
        {
            StatusText.Text = "Could not locate the browser window. Ensure page is visible.";
            return;
        }

        StatusText.Text = "Extracting page contentâ€¦";
        try
        {
            // WebView2's accessibility tree lives in our process, so capture on the UI thread.
            // Chromium builds its UIA tree lazily: the first full request triggers generation,
            // so warm up once, then capture for real.
            UiaCaptureService.CaptureWindow(webHwnd);
            await Task.Delay(silent ? 800 : 1200);
            var raw = UiaCaptureService.CaptureWindow(webHwnd);
            var doc = DocumentNormalizer.Normalize(raw,
                title: WebView.CoreWebView2.DocumentTitle ?? UrlBox.Text,
                source: "Website");
            _lastDoc = doc;
            _lastSourceHwnd = webHwnd;
            _lastSourceIsWeb = true;
            LastSourceText.Text = doc.Title;
            LastSummaryText.Text = $"{doc.Blocks.Count} blocks Â· {doc.PlainText.Length:N0} characters";

            if (string.IsNullOrWhiteSpace(doc.PlainText))
            {
                StatusText.Text = "Extracted no readable text â€” the page may block automation. Try waiting longer.";
                return;
            }

            var ov = EnsureOverlay(doc);
            if (!silent)
            {
                PositionNear(ov, hostHwnd);
                if (_settings.SpeakOnCapture)
                    App.Speech.Speak($"{doc.Title}. {doc.PlainText}");
                ov.ShowAndFocusFirst();
                if (!_pressExecuted)
                {
                    _pressExecuted = true;
                    var pressArg = Environment.GetCommandLineArgs().SkipWhile(a => a != "--press").Skip(1).FirstOrDefault();
                    if (int.TryParse(pressArg, out var pressIndex))
                        ov.SelectAndActivate(pressIndex);
                }
                StatusText.Text = $"Readable view of {doc.Title} ready.";
            }
            else if (!ov.IsVisible)
            {
                ov.ShowOverlayNoActivate();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Extraction failed: {ex.Message}";
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // The close button only dismisses the optional control panel.
        // The hidden host must remain alive so the global hotkey continues to work.
        e.Cancel = true;
        _gameAutoReadTimer?.Stop();
        Hide();
    }

    /// <summary>
    /// WPF only activates a focused button with Space, never Enter. Screen-reader
    /// users press Enter, so route Enter on a focused button to its Click.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.FocusedElement is Button btn)
        {
            e.Handled = true;
            btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
    }

    // ─── Game Scan ────────────────────────────────────────────────────────────

    private void GameScanBtn_Click(object sender, RoutedEventArgs e) => GameScanAsync();

    private void GameSpeakAll_Click(object sender, RoutedEventArgs e)
    {
        if (_lastGameDoc is null) { GameStatusText.Text = "Scan a game first."; return; }
        App.Speech.Speak(_lastGameDoc.PlainText);
    }

    private void GameAutoRead_Changed(object sender, RoutedEventArgs e)
    {
        if (GameAutoReadCheck.IsChecked == true)
        {
            _gameAutoReadTimer ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.5),
            };
            _gameAutoReadTimer.Tick += GameAutoReadTimer_Tick;
            _gameAutoReadTimer.Start();
            GameStatusText.Text = "Auto-announcing new game text every 2.5 s.";
        }
        else
        {
            if (_gameAutoReadTimer is not null)
            {
                _gameAutoReadTimer.Tick -= GameAutoReadTimer_Tick;
                _gameAutoReadTimer.Stop();
            }
            GameStatusText.Text = string.Empty;
        }
    }

    private void GameAutoReadTimer_Tick(object? sender, EventArgs e) => GameScanAsync(silent: true);

    /// <summary>
    /// Captures the current foreground window using OCR and shows the result in the reader overlay.
    /// Works with any game engine: Frostbite, Unreal Engine, Source, Unity, id Tech, etc.
    /// Frostbite detection: looks for EA/DICE process names and adjusts confidence thresholds.
    /// </summary>
    private async void GameScanAsync(bool silent = false)
    {
        if (_gameScanInFlight) return;
        _gameScanInFlight = true;
        if (!silent) GameStatusText.Text = "Scanning game screen\u2026";

        // Snapshot the foreground window *before* we lose it
        var hwnd = NativeMethods.GetForegroundWindow();
        var procName = string.Empty;
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            procName = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
        }
        catch { }

        // Frostbite engine games: bf2042, battlefield*, fifa*, nhl*, nba*, etc.
        // Frostbite renders entirely via DirectX/DX11 with no UIA accessibility tree —
        // OCR is the only way in, so we reduce the confidence threshold for sparser layouts.
        var isFrostbite = procName is "bf2042" or "bf1" or "bfv" or "bfm"
            || procName.StartsWith("battlefield", StringComparison.Ordinal)
            || procName.StartsWith("fifa", StringComparison.Ordinal)
            || procName.StartsWith("nhl", StringComparison.Ordinal)
            || procName.StartsWith("nba", StringComparison.Ordinal)
            || procName.StartsWith("starwars", StringComparison.Ordinal)
            || procName is "masseffect" or "dragonage" or "anthem";

        float minConfidence = isFrostbite ? 0.55f : 0.70f;

        try
        {
            DiagnosticsLogger.Log($"Game scan triggered hwnd=0x{hwnd.ToInt64():X} proc={procName} minConfidence={minConfidence}");
            var doc = await Task.Run(() =>
                OcrCaptureService.CaptureAsync(hwnd, minConfidence, true).GetAwaiter().GetResult());

            _lastGameDoc = doc;

            var blockCount = doc.Blocks.Count;
            var engineTag = isFrostbite ? " [Frostbite]" : string.Empty;
            GameStatusText.Text = $"Found {blockCount} text element{(blockCount == 1 ? "" : "s")}{engineTag}.";
            LastSourceText.Text = doc.Title;
            LastSummaryText.Text = $"{blockCount} game blocks \u00b7 {doc.PlainText.Length:N0} chars";

            if (blockCount == 0)
            {
                if (!silent) App.Speech.Speak("No text found on screen. Try again when a menu is open.");
                return;
            }

            // Diff against last game scan; only speak new content during auto-poll
            var prevText = _lastGameDoc?.PlainText ?? string.Empty;
            var newText = doc.PlainText;
            var changed = string.IsNullOrWhiteSpace(prevText)
                || !string.Equals(prevText.Trim(), newText.Trim(), StringComparison.OrdinalIgnoreCase);

            _lastGameDoc = doc;
            _lastDoc = doc;
            _lastSourceIsWeb = false;
            _lastSourceHwnd = hwnd;

            // Game scans live in their own window. Manual scans show and focus it;
            // silent auto-read polls only update it (never re-show it after a hide).
            var ov = EnsureGameOverlay(doc);
            if (!silent)
                ov.ShowAndFocusFirst();

            if (!silent)
                App.Speech.Speak($"Game scan complete. {blockCount} menu item{(blockCount == 1 ? "" : "s")} found.");
            else if (changed && !string.IsNullOrWhiteSpace(newText))
                App.Speech.Speak(newText);
        }
        catch (Exception ex)
        {
            GameStatusText.Text = $"Scan failed: {ex.Message}";
        }
        finally
        {
            _gameScanInFlight = false;
        }
    }
}
