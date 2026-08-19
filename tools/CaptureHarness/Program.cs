using System.Runtime.InteropServices;
using AccessOverlay.Core.AppCapture;
using AccessOverlay.Core.Normalization;
using AccessOverlay.Core.Nvda;

// CLI tool for inspecting what the overlay pipeline produces from a real window.
// Usage: CaptureHarness [window-title-substring]
//   - captures the window whose title contains the substring (default: foreground window)

var speak = args.Contains("--speak");
var dumpTree = args.Contains("--tree");
var dumpWindows = args.Contains("--window-hierarchy");
var webviewOf = args.Contains("--webview-of");
var search = args.FirstOrDefault(a => !a.StartsWith("--"));
IntPtr hwnd = IntPtr.Zero;

if (search is null)
{
    hwnd = GetForegroundWindow();
}
else
{
    hwnd = FindWindowByTitle(search);
}

if (hwnd == IntPtr.Zero)
{
    Console.Error.WriteLine($"No window found for '{search}'");
    return 1;
}

if (dumpWindows)
{
    EnumWindows((top, _) =>
    {
        var t = UiaCaptureService.GetWindowTitle(top);
        if (t.Contains("Access Overlay", StringComparison.OrdinalIgnoreCase))
            DumpWindowHierarchy(top, 0);
        return true;
    }, IntPtr.Zero);
    return 0;
}

if (webviewOf)
{
    hwnd = FindWebView2Child(hwnd);
    if (hwnd == IntPtr.Zero)
    {
        Console.Error.WriteLine("No Chromium window found under that window.");
        return 1;
    }
    Console.WriteLine($"WebView hwnd: 0x{hwnd.ToInt64():X} title='{UiaCaptureService.GetWindowTitle(hwnd)}'");
}

var title = UiaCaptureService.GetWindowTitle(hwnd);
Console.WriteLine($"=== Capturing window: {title} (hwnd 0x{hwnd.ToInt64():X}) ===");
Console.WriteLine($"NVDA running: {NvdaClient.IsRunning}");
if (NvdaClient.IsRunning)
{
    Console.WriteLine($"NVDA pid: {NvdaClient.GetProcessId()}");
    if (speak)
    {
        Console.WriteLine("Asking NVDA to speak a test phrase…");
        Console.WriteLine($"nvdaController_speakText result: {(NvdaClient.Speak("Access overlay connected. This is spoken through NVDA.", interrupt: false) ? "0 (ok)" : "failed")}");
    }
}

try
{
    var raw = UiaCaptureService.CaptureWindow(hwnd);
    if (dumpTree)
    {
        DumpNode(raw, 0);
        return 0;
    }
    var doc = DocumentNormalizer.Normalize(raw, title: title, source: "harness");
    Console.WriteLine($"--- {doc.Blocks.Count} blocks, {doc.PlainText.Length} chars ---");
    foreach (var b in doc.Blocks)
    {
        var indent = new string(' ', b.Depth * 2);
        var tag = b switch
        {
            AccessOverlay.Core.Model.HeadingBlock h => $"H{h.Level} ",
            AccessOverlay.Core.Model.ControlBlock c => $"[{c.Role}] ",
            AccessOverlay.Core.Model.ListItemBlock => "• ",
            _ => "",
        };
        Console.WriteLine($"{indent}{tag}{b.Text}");
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Capture failed: {ex.Message}");
    return 2;
}

static IntPtr FindWindowByTitle(string substring)
{
    IntPtr contains = IntPtr.Zero;
    IntPtr exact = IntPtr.Zero;
    EnumWindows((hwnd, _) =>
    {
        if (!IsWindowVisible(hwnd)) return true;
        var title = UiaCaptureService.GetWindowTitle(hwnd);
        if (title.Equals(substring, StringComparison.OrdinalIgnoreCase))
        {
            exact = hwnd;
            return false;
        }
        if (contains == IntPtr.Zero && title.Contains(substring, StringComparison.OrdinalIgnoreCase))
            contains = hwnd;
        return true;
    }, IntPtr.Zero);
    return exact != IntPtr.Zero ? exact : contains;
}

static IntPtr FindWebView2Child(IntPtr parent)
{
    var queue = new Queue<IntPtr>();
    queue.Enqueue(parent);
    IntPtr fallback = IntPtr.Zero;
    while (queue.Count > 0)
    {
        var current = queue.Dequeue();
        IntPtr child = IntPtr.Zero;
        while ((child = FindWindowEx(current, child, null, null)) != IntPtr.Zero)
        {
            var cls = GetWindowClass(child);
            if (cls.StartsWith("Chrome_WidgetWin_", StringComparison.OrdinalIgnoreCase))
            {
                if (fallback == IntPtr.Zero) fallback = child;
                if (UiaCaptureService.GetWindowTitle(child).Length > 0)
                    return child;
            }
            queue.Enqueue(child);
        }
    }
    return fallback;
}

static void DumpWindowHierarchy(IntPtr hwnd, int depth)
{
    var title = UiaCaptureService.GetWindowTitle(hwnd);
    var cls = GetWindowClass(hwnd);
    var visible = IsWindowVisible(hwnd) ? "vis" : "hid";
    Console.WriteLine($"{new string(' ', depth * 2)}0x{hwnd.ToInt64():X} [{cls}] ({visible}) '{title}'");
    IntPtr child = IntPtr.Zero;
    while ((child = FindWindowEx(hwnd, child, null, null)) != IntPtr.Zero)
        DumpWindowHierarchy(child, depth + 1);
}

[DllImport("user32.dll", EntryPoint = "GetClassName", CharSet = CharSet.Unicode)]
static extern int GetClassNameWin32(IntPtr hWnd, System.Text.StringBuilder className, int maxCount);

static string GetWindowClass(IntPtr hwnd)
{
    var sb = new System.Text.StringBuilder(256);
    GetClassNameWin32(hwnd, sb, sb.Capacity);
    return sb.ToString();
}

[DllImport("user32.dll", SetLastError = true)]
static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

static void DumpNode(AccessOverlay.Core.Model.RawNode node, int depth)
{
    var name = (node.Name ?? "").Replace("\n", "\\n");
    if (name.Length > 120) name = name[..120] + "…";
    var heading = node.HeadingLevel > 0 ? $" (H{node.HeadingLevel})" : "";
    var off = node.IsOffscreen ? " [OFFSCREEN]" : "";
    var toggle = node.ToggleState.Length > 0 ? $" [{node.ToggleState}]" : "";
    Console.WriteLine($"{new string(' ', depth * 2)}{node.ControlType}{heading}{off}{toggle}: {name}");
    foreach (var child in node.Children)
        DumpNode(child, depth + 1);
}

[DllImport("user32.dll")]
static extern IntPtr GetForegroundWindow();

[DllImport("user32.dll")]
static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

[DllImport("user32.dll")]
static extern bool IsWindowVisible(IntPtr hWnd);

delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
