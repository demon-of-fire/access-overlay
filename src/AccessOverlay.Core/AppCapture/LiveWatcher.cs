using System.Runtime.InteropServices;

namespace AccessOverlay.Core.AppCapture;

/// <summary>
/// Watches the UI Automation event stream (SetWinEventHook) for changes inside the
/// window the reader is currently showing: new/changed/hidden elements, name and
/// value changes, focus moves. Events are coalesced through a debounce timer so a
/// burst of changes (a page loading, a list re-rendering) results in a single
/// content refresh instead of a capture storm.
/// </summary>
public sealed class LiveWatcher : IDisposable
{
    // EVENT_OBJECT_* range covering create/destroy/show/hide/reorder/focus/
    // selection/state/location/name/description/value changes.
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectValueChange = 0x800E;
    private const uint WineventOutOfContext = 0x0000;

    // Sentinel idObject values for the mouse pointer and the text caret. Their
    // location changes fire constantly (caret blink, pointer hover) and never
    // mean the readable content changed, so they must not trigger a refresh.
    private const int ObjidCursor = unchecked((int)0xFFFFFFFA);
    private const int ObjidCaret = unchecked((int)0xFFFFFFF8);

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private readonly WinEventDelegate _callback;
    private readonly IntPtr _hook;
    private readonly System.Threading.Timer _debounce;
    private readonly SynchronizationContext? _syncContext;
    private readonly TimeSpan _delay;
    private IntPtr _tracked;
    private int _dirty;

    public LiveWatcher(TimeSpan debounce, SynchronizationContext? syncContext = null)
    {
        _delay = debounce;
        _callback = OnWinEvent;
        _syncContext = syncContext ?? SynchronizationContext.Current;
        _debounce = new System.Threading.Timer(Flush, null, Timeout.Infinite, Timeout.Infinite);
        _hook = SetWinEventHook(EventObjectCreate, EventObjectValueChange, IntPtr.Zero, _callback, 0, 0, WineventOutOfContext);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("SetWinEventHook failed.");
    }

    /// <summary>Raised (on the captured synchronization context) when the tracked window's content may have changed.</summary>
    public event Action<IntPtr>? ContentChanged;

    /// <summary>Sets the window whose content changes should trigger a refresh.</summary>
    public void Track(IntPtr hwnd) => _tracked = hwnd;

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero) return;
        var tracked = _tracked;
        if (tracked == IntPtr.Zero) return;

        // The pointer and text caret churn constantly; ignore them entirely.
        if (idObject == ObjidCursor || idObject == ObjidCaret) return;

        // Only the tracked window and its descendants matter; everything else
        // (our own overlay, other apps) must not cause a refresh.
        if (hwnd != tracked && !IsChild(tracked, hwnd)) return;

        if (Interlocked.Exchange(ref _dirty, 1) == 0)
            _debounce.Change((int)_delay.TotalMilliseconds, Timeout.Infinite);
    }

    private void Flush(object? state)
    {
        Interlocked.Exchange(ref _dirty, 0);
        var tracked = _tracked;
        if (tracked == IntPtr.Zero) return;

        if (_syncContext is not null)
        {
            _syncContext.Post(_ => ContentChanged?.Invoke(tracked), null);
            return;
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ContentChanged?.Invoke(tracked));
            return;
        }

        ContentChanged?.Invoke(tracked);
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

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
}
