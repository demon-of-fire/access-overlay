using System.Runtime.InteropServices;

namespace AccessOverlay.App.Native;

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    internal static void SendClickAt(int x, int y)
    {
        if (!GetCursorPos(out var original))
        {
            SetCursorPos(x, y);
            mouse_event(MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP, (uint)x, (uint)y, 0, UIntPtr.Zero);
            return;
        }

        SetCursorPos(x, y);
        mouse_event(MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP, (uint)x, (uint)y, 0, UIntPtr.Zero);
        SetCursorPos(original.X, original.Y);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    /// <summary>True when <paramref name="hwnd"/> is a descendant of <paramref name="parent"/>.</summary>
    internal static bool IsChildWindow(IntPtr parent, IntPtr hwnd)
        => parent != IntPtr.Zero && hwnd != IntPtr.Zero && IsChild(parent, hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Shows a window without activating it (doesn't steal focus from the underlying page/app).</summary>
    internal static void ShowNoActivate(IntPtr hwnd)
    {
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
    }

    /// <summary>Allows a window that was shown with no-activate to take focus normally again.</summary>
    internal static void AllowActivate(IntPtr hwnd)
    {
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) & ~WS_EX_NOACTIVATE);
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>
    /// Finds the Chromium content HWND anywhere under a parent window, or the parent
    /// itself when it is already the browser widget. Chromium nests its windows
    /// (Chrome_WidgetWin_0 wrapper → Chrome_WidgetWin_1 with the actual page). We prefer
    /// the one carrying a window title, since that is the page window whose UI
    /// Automation tree contains the DOM. Desktop apps built on the same engine
    /// (various store/launcher clients) resolve the same way.
    /// </summary>
    internal static IntPtr FindWebView2Hwnd(IntPtr parent)
    {
        var selfClass = GetWindowClassName(parent);
        if (selfClass.StartsWith("Chrome_WidgetWin_", StringComparison.OrdinalIgnoreCase)
            && GetWindowTextSafe(parent).Length > 0)
            return parent;

        var queue = new Queue<IntPtr>();
        queue.Enqueue(parent);
        IntPtr fallback = IntPtr.Zero;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            IntPtr child = IntPtr.Zero;
            while ((child = FindWindowEx(current, child, null, null)) != IntPtr.Zero)
            {
                var cls = GetWindowClassName(child);
                if (cls.StartsWith("Chrome_WidgetWin_", StringComparison.OrdinalIgnoreCase))
                {
                    if (fallback == IntPtr.Zero) fallback = child;
                    if (GetWindowTextSafe(child).Length > 0)
                        return child;
                }
                queue.Enqueue(child);
            }
        }
        return fallback;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    internal static string GetWindowClassName(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    internal static string GetWindowTextSafe(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
