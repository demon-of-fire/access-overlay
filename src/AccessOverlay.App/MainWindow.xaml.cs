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

namespace AccessOverlay.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly HotkeyManager? _hotkey;
    private ForegroundWatcher? _watcher;
    private LiveWatcher? _liveWatcher;
    private OverlayWindow? _overlay;
    private ReadableDocument? _lastDoc;
    private IntPtr _lastSourceHwnd;
    private bool _lastSourceIsWeb;
    private bool _captureInFlight;
    private (IntPtr Hwnd, bool Auto)? _pendingCapture;
    private bool _webReadInFlight;
    private bool? _pendingWebReadSilent;
    private bool _pressExecuted;
    private bool _suppressAutoFollowPersistence;
    /// <summary>Set when the user deliberately dismissed the reader; auto-refresh may update it in place but must never re-show it.</summary>
    private bool _userHidden;
    private DateTime _lastContentCapture = DateTime.MinValue;
    private bool _contentDirty;
    private System.Windows.Threading.DispatcherTimer? _refreshTimer;
    private bool _lastDocWasOcr;

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
        // Auto-follow is the default: the reader keeps up with new screens by itself.
        AutoFollowCheck.IsChecked = _settings.AutoFollow;
        // The window being shown always stays current; the checkbox only decides
        // whether the reader also jumps to other apps.
        StartLiveRefresh();
        if (_settings.AutoFollow) StartWatcher();

        _hotkey = new HotkeyManager(this, _settings.Hotkey);
        if (_hotkey.IsRegistered)
            _hotkey.HotkeyPressed += HotkeyPressed;
        else
            StatusText.Text = $"Hotkey {_settings.Hotkey} could not be registered (in use elsewhere?).";

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
        _userHidden = false;
        EnsureOverlay(new ReadableDocument());
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd != IntPtr.Zero && GetPid(hwnd) != Environment.ProcessId && !IsShellOrDesktop(hwnd))
        {
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

    private void StartLiveRefresh()
    {
        try
        {
            _liveWatcher = new LiveWatcher(TimeSpan.FromMilliseconds(600));
            _liveWatcher.ContentChanged += OnContentChanged;
        }
        catch
        {
            // The timer below still refreshes on its regular interval.
            StatusText.Text = "Content watching unavailable.";
        }

        _refreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
    }

    private void StartWatcher()
    {
        try
        {
            _watcher ??= new ForegroundWatcher(TimeSpan.FromMilliseconds(350));
            _watcher.ForegroundChanged -= OnForegroundChanged;
            _watcher.ForegroundChanged += OnForegroundChanged;
        }
        catch
        {
            StatusText.Text = "Foreground watching unavailable.";
        }
    }

    private void StopWatcher()
    {
        if (_watcher is not null)
        {
            _watcher.ForegroundChanged -= OnForegroundChanged;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    /// <summary>
    /// The window we are showing changed underneath us (new dialog, new page,
    /// content re-rendered). Only noted here: <see cref="RefreshTimer_Tick"/> does
    /// the work, so changes during a busy moment are delayed rather than lost.
    /// </summary>
    private void OnContentChanged(IntPtr hwnd)
    {
        if (_lastSourceHwnd == IntPtr.Zero) return;
        if (hwnd != _lastSourceHwnd && !IsChildOf(_lastSourceHwnd, hwnd)) return;
        _contentDirty = true;
    }

    /// <summary>
    /// Keeps the reader in step with its source: within a second of a reported
    /// change, and every few seconds regardless, because web content and some apps
    /// change without reporting it. Unchanged captures don't touch the reader.
    /// </summary>
    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_overlay is not { IsVisible: true, IsEditing: false }) return;
        if (_lastSourceHwnd == IntPtr.Zero || _captureInFlight || _webReadInFlight) return;
        if (!_lastSourceIsWeb && (!NativeMethods.IsWindow(_lastSourceHwnd) || NativeMethods.IsIconic(_lastSourceHwnd))) return;

        var sinceLast = DateTime.UtcNow - _lastContentCapture;
        // A screen read hides the reader for its picture, so it stays occasional.
        var due = _lastDocWasOcr
            ? _contentDirty && sinceLast >= TimeSpan.FromSeconds(5) && !_overlay.IsActive
            : _contentDirty || sinceLast >= TimeSpan.FromSeconds(3);
        if (!due) return;

        _contentDirty = false;
        _lastContentCapture = DateTime.UtcNow;
        DiagnosticsLogger.Log("Auto-refreshing reader");
        if (_lastSourceIsWeb)
            ReadCurrentWebPage(silent: true);
        else
            CaptureWindow(_lastSourceHwnd, auto: true);
    }

    private static bool IsChildOf(IntPtr parent, IntPtr hwnd)
    {
        try { return NativeMethods.IsChildWindow(parent, hwnd); }
        catch { return false; }
    }

    private void OnForegroundChanged(IntPtr hwnd)
    {
        DiagnosticsLogger.Log($"Foreground changed hwnd=0x{hwnd.ToInt64():X}");
        if (hwnd == IntPtr.Zero) return;
        var pid = GetPid(hwnd);
        if (pid == Environment.ProcessId) return;
        if (IsShellOrDesktop(hwnd)) return;
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
        // Ctrl+Alt+A: hidden -> capture the focused app and show it; showing behind
        // another app -> move the keyboard into it; already in it -> hide it.
        if (_overlay?.IsVisible == true)
        {
            if (!_overlay.IsActive)
            {
                _overlay.FocusReader();
                return;
            }
            _overlay.Hide();
            _userHidden = true;
            return;
        }

        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || GetPid(hwnd) == Environment.ProcessId)
        {
            StatusText.Text = "Select another app, then press the hotkey again.";
            return;
        }

        // Explicit request: the reader may show itself again after a dismissal.
        _userHidden = false;
        CaptureWindow(hwnd, auto: false);
    }

    private void CaptureBtn_Click(object sender, RoutedEventArgs e)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        CaptureWindow(hwnd, auto: false);
    }

    private async void CaptureWindow(IntPtr hwnd, bool auto)
    {
        if (hwnd == IntPtr.Zero) return;
        if (_captureInFlight)
        {
            // Dropping this would leave the reader on an out-of-date screen. Only the
            // newest request matters, but an explicit one stays explicit.
            _pendingCapture = (hwnd, auto && (_pendingCapture?.Auto ?? true));
            return;
        }

        _captureInFlight = true;
        try
        {
            await CaptureWindowCoreAsync(hwnd, auto);
        }
        finally
        {
            _captureInFlight = false;
            if (_pendingCapture is { } next)
            {
                _pendingCapture = null;
                CaptureWindow(next.Hwnd, next.Auto);
            }
        }
    }

    private async Task CaptureWindowCoreAsync(IntPtr hwnd, bool auto)
    {
        StatusText.Text = "Capturing…";
        DiagnosticsLogger.Log($"CaptureWindow started hwnd=0x{hwnd.ToInt64():X} auto={auto}");

        // Some desktop apps host their entire UI inside an embedded browser child
        // window. Capture that child instead of the outer shell, and warm up its
        // lazily-built accessibility tree first (the initial request triggers
        // generation; the second one actually has content).
        var target = hwnd;
        var isEmbeddedBrowser = false;
        try
        {
            var child = NativeMethods.FindWebView2Hwnd(hwnd);
            if (child != IntPtr.Zero && child != hwnd)
            {
                target = child;
                isEmbeddedBrowser = true;
            }
            else
            {
                isEmbeddedBrowser = NativeMethods.GetWindowClassName(hwnd)
                    .StartsWith("Chrome_WidgetWin_", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { /* window vanished; capture the top-level and let it fail normally */ }

        const string sourceLabel = "Foreground app";
        var r = await Task.Run(() =>
        {
            try
            {
                if (isEmbeddedBrowser)
                {
                    try { UiaCaptureService.CaptureWindow(target); } catch { }
                    System.Threading.Thread.Sleep(700);
                }
                var raw = UiaCaptureService.CaptureWindow(target);
                var title = UiaCaptureService.GetWindowTitle(target);
                if (title.Length == 0) title = UiaCaptureService.GetWindowTitle(hwnd);
                var doc = DocumentNormalizer.Normalize(raw, title: title, source: sourceLabel);
                return (Ok: true, Doc: doc, Title: title);
            }
            catch (Exception ex)
            {
                return (Ok: false, Doc: (ReadableDocument?)null, Title: ex.Message);
            }
        });

        if (!r.Ok || r.Doc is null)
        {
            StatusText.Text = $"Capture failed: {r.Title}";
            return;
        }

        // Custom-drawn interfaces expose (at best) a nearly empty UIA tree.
        // Fall back to reading the screen itself so the content is still
        // usable, with every line clickable.
        if (IsThinDocument(r.Doc))
        {
            DiagnosticsLogger.Log("UIA tree too thin; falling back to screen OCR");
            await CaptureViaOcrFallback(hwnd, auto, r.Title);
            return;
        }

        ApplyCapturedDoc(r.Doc, hwnd, auto, r.Title);
    }

    /// <summary>True when a capture found essentially nothing worth showing.</summary>
    /// <remarks>
    /// A short form is not thin: OCR output can't be typed into, so a document with
    /// a real edit field must stay on the accessibility path.
    /// </remarks>
    private static bool IsThinDocument(ReadableDocument doc)
        => (doc.Blocks.Count < 3 || doc.PlainText.Trim().Length < 60)
           && !doc.Blocks.Any(b => b is ControlBlock { Role: "edit field" });

    /// <summary>
    /// Last resort for interfaces that do not expose their content through
    /// accessibility at all: hide the overlay (so it is not in its own picture),
    /// screenshot the window, OCR it, and present the result. Every OCR line is
    /// clickable through the existing coordinate-click path.
    /// </summary>
    private async Task CaptureViaOcrFallback(IntPtr hwnd, bool auto, string title)
    {
        if (_overlay?.IsEditing == true) return;
        var wasVisible = _overlay?.IsVisible == true;
        var wasActive = _overlay?.IsActive == true;
        if (wasVisible) _overlay!.Hide();
        await Task.Delay(250); // let the screen repaint without us in the way

        ReadableDocument doc;
        try
        {
            doc = await OcrCaptureService.CaptureAsync(hwnd);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Capture failed: {ex.Message}";
            if (wasVisible) _overlay!.ShowOverlayNoActivate();
            return;
        }

        if (string.IsNullOrWhiteSpace(title) == false)
            doc.Title = title;
        ApplyCapturedDoc(doc, hwnd, auto, title, ocrFallback: true);
        if (wasVisible && !_overlay!.IsVisible) _overlay.ShowOverlayNoActivate();
        if (wasActive) _overlay!.FocusReader();
    }

    private void ApplyCapturedDoc(ReadableDocument doc, IntPtr hwnd, bool auto, string title, bool ocrFallback = false)
    {
        _lastDoc = doc;
        _lastSourceHwnd = hwnd;
        _lastSourceIsWeb = false;
        _lastDocWasOcr = ocrFallback;
        _liveWatcher?.Track(hwnd);
        LastSourceText.Text = doc.Title;
        LastSummaryText.Text = $"{doc.Blocks.Count} blocks · {doc.PlainText.Length:N0} characters";

        if (_overlay is not null && _overlay.IsVisible)
        {
            // Follow the app the user moved to: update silently, don't steal focus.
            _overlay.UpdateDocument(doc);
        }
        else if (auto)
        {
            if (_userHidden) return; // user dismissed the reader; don't pop back up

            // The overlay runs on its own: first capture opens it without stealing focus.
            EnsureOverlay(doc).ShowOverlayNoActivate();
        }
        else
        {
            var ov = EnsureOverlay(doc);
            PositionNear(ov, hwnd);
            ov.ShowAndFocusFirst();
        }
        StatusText.Text = ocrFallback ? $"Captured (screen read): {title}" : $"Captured: {title}";
    }

    private OverlayWindow EnsureOverlay(ReadableDocument doc)
    {
        if (_overlay is null)
        {
            _overlay = new OverlayWindow(doc);
            _overlay.ControlActivated += OnControlActivated;
            _overlay.RefreshRequested += OnRefreshRequested;
            _overlay.ControlPanelRequested += (_, _) => ShowControlPanel();
            _overlay.HiddenByUser += (_, _) => _userHidden = true;
        }
        else
        {
            _overlay.UpdateDocument(doc);
        }
        return _overlay;
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
        // Once the user has a place for the reader, it stays there.
        if (overlay.HasSavedPosition) return;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;

        // GetWindowRect is in device pixels; WPF window placement isn't.
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = toDip.Transform(new Point(rect.Left, rect.Top));
        var bottomRight = toDip.Transform(new Point(rect.Right, rect.Bottom));

        var work = SystemParameters.WorkArea;
        var w = Math.Min(overlay.Width, work.Width * 0.92);
        var h = Math.Min(overlay.Height, work.Height * 0.92);
        double left = bottomRight.X - 40;
        if (left + w > work.Right) left = Math.Max(work.Left, topLeft.X + 40 - w);
        var top = Math.Max(work.Top, topLeft.Y + 30);
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
            StatusText.Text = "Nothing captured yet — press Ctrl+Alt+A or click Capture.";
            return;
        }
        _userHidden = false;
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
        StatusText.Text = $"Loading {url}…";
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
        StatusText.Text = "Page loaded — extracting readable view…";
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
        if (_webReadInFlight)
        {
            _pendingWebReadSilent = (_pendingWebReadSilent ?? true) && silent;
            return;
        }

        _webReadInFlight = true;
        try
        {
            await ReadCurrentWebPageCoreAsync(silent);
        }
        finally
        {
            _webReadInFlight = false;
            if (_pendingWebReadSilent is { } nextSilent)
            {
                _pendingWebReadSilent = null;
                ReadCurrentWebPage(nextSilent);
            }
        }
    }

    private async Task ReadCurrentWebPageCoreAsync(bool silent)
    {
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

        StatusText.Text = "Extracting page content…";
        try
        {
            // Chromium builds its UIA tree lazily: the first full request triggers
            // generation, so warm up once, then capture for real.
            var raw = await Task.Run(async () =>
            {
                UiaCaptureService.CaptureWindow(webHwnd);
                await Task.Delay(silent ? 800 : 1200);
                return UiaCaptureService.CaptureWindow(webHwnd);
            });
            var doc = DocumentNormalizer.Normalize(raw,
                title: WebView.CoreWebView2.DocumentTitle ?? UrlBox.Text,
                source: "Website");
            _lastDoc = doc;
            _lastSourceHwnd = webHwnd;
            _lastSourceIsWeb = true;
            _lastDocWasOcr = false;
            _liveWatcher?.Track(webHwnd);
            LastSourceText.Text = doc.Title;
            LastSummaryText.Text = $"{doc.Blocks.Count} blocks · {doc.PlainText.Length:N0} characters";

            if (string.IsNullOrWhiteSpace(doc.PlainText))
            {
                StatusText.Text = "Extracted no readable text — the page may block automation. Try waiting longer.";
                return;
            }

            var ov = EnsureOverlay(doc);
            if (!silent)
            {
                PositionNear(ov, hostHwnd);
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
            else if (!ov.IsVisible && !_userHidden)
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
        Hide();
    }

    /// <summary>
    /// WPF only activates a focused button with Space, never Enter. Screen-reader
    /// users press Enter, so route Enter on a focused button to its Click.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F7)
        {
            e.Handled = true;
            DiagnosticsLogger.Enabled = !DiagnosticsLogger.Enabled;
            StatusText.Text = DiagnosticsLogger.Enabled ? "Debug logging on." : "Debug logging off.";
            return;
        }

        if (e.Key == Key.Enter && Keyboard.FocusedElement is Button btn)
        {
            e.Handled = true;
            btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
    }
}
