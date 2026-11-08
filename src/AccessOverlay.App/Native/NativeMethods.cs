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
        var hadCursor = GetCursorPos(out var original);
        SetCursorPos(x, y);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        // Input is queued; moving the cursor straight back can land the click there instead.
        System.Threading.Thread.Sleep(40);
        if (hadCursor) SetCursorPos(original.X, original.Y);
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_DELETE = 0x2E;
    private const ushort VK_A = 0x41;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    // MOUSEINPUT is the largest member and sets the size SendInput validates.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    private static INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };

    private static void Send(List<INPUT> inputs)
    {
        if (inputs.Count > 0)
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    /// <summary>Presses Ctrl+A in whatever currently has the keyboard.</summary>
    internal static void SendSelectAll() => Send(new List<INPUT>
    {
        Key(VK_CONTROL, 0, 0),
        Key(VK_A, 0, 0),
        Key(VK_A, 0, KEYEVENTF_KEYUP),
        Key(VK_CONTROL, 0, KEYEVENTF_KEYUP),
    });

    /// <summary>Types text as Unicode keystrokes, independent of the keyboard layout. Empty text deletes the selection.</summary>
    internal static void SendText(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2 + 2);
        if (text.Length == 0)
        {
            inputs.Add(Key(VK_DELETE, 0, 0));
            inputs.Add(Key(VK_DELETE, 0, KEYEVENTF_KEYUP));
        }
        foreach (var c in text.Replace("\r\n", "\n"))
        {
            if (c == '\n')
            {
                inputs.Add(Key(VK_RETURN, 0, 0));
                inputs.Add(Key(VK_RETURN, 0, KEYEVENTF_KEYUP));
                continue;
            }
            inputs.Add(Key(0, c, KEYEVENTF_UNICODE));
            inputs.Add(Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        Send(inputs);
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
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    /// <summary>True when <paramref name="hwnd"/> is a descendant of <paramref name="parent"/>.</summary>
    internal static bool IsChildWindow(IntPtr parent, IntPtr hwnd)
        => parent != IntPtr.Zero && hwnd != IntPtr.Zero && IsChild(parent, hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>
    /// Brings our window forward when another app just took the foreground, which
    /// the system otherwise refuses. Sharing input with the current foreground
    /// thread for the duration of the call is what permits it.
    /// </summary>
    internal static void ForceForeground(IntPtr hwnd)
    {
        var me = GetCurrentThreadId();
        var other = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var attached = other != 0 && other != me && AttachThreadInput(me, other, true);
        try { SetForegroundWindow(hwnd); }
        finally { if (attached) AttachThreadInput(me, other, false); }
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
