using System.Runtime.InteropServices;

namespace AccessOverlay.Core.AppCapture;

/// <summary>
/// Watches for foreground-window changes (SetWinEventHook) so the overlay can
/// follow the app the user is actually working in. Events are debounced and
/// coalesced to avoid hammering during Alt-Tab or app startup.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private readonly WinEventDelegate _callback;
    private readonly IntPtr _hook;
    private readonly System.Threading.Timer _debounce;
    private readonly SynchronizationContext? _syncContext;
    private IntPtr _pendingHwnd;
    private int _flushPending;

    public ForegroundWatcher(TimeSpan debounce, SynchronizationContext? syncContext = null)
    {
        _callback = OnWinEvent;
        _syncContext = syncContext ?? SynchronizationContext.Current;
        _debounce = new System.Threading.Timer(Flush, null, Timeout.Infinite, Timeout.Infinite);
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback, 0, 0, WineventOutOfContext | WineventSkipOwnProcess);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("SetWinEventHook failed.");
    }

    public event Action<IntPtr>? ForegroundChanged;

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero) return;
        _pendingHwnd = hwnd;
        if (Interlocked.CompareExchange(ref _flushPending, 1, 0) == 0)
            _debounce.Change(350, Timeout.Infinite);
    }

    private void Flush(object? state)
    {
        Interlocked.Exchange(ref _flushPending, 0);
        var hwnd = _pendingHwnd;
        if (hwnd == IntPtr.Zero) return;
        
        if (_syncContext is not null)
        {
            _syncContext.Post(_ => ForegroundChanged?.Invoke(hwnd), null);
            return;
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ForegroundChanged?.Invoke(hwnd));
            return;
        }

        ForegroundChanged?.Invoke(hwnd);
    }

    public void Dispose()
    {
        _debounce.Dispose();
        if (_hook != IntPtr.Zero)
            UnhookWinEvent(_hook);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
}
