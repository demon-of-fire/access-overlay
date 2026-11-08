using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using AccessOverlay.Core.Model;

namespace AccessOverlay.Core.AppCapture;

/// <summary>
/// Reads a window's UI Automation tree into the neutral <see cref="RawNode"/> model.
/// One pipeline is used for both desktop apps and WebView2-hosted websites,
/// because Chromium's WebView2 exposes its DOM through the same UIA interface.
/// </summary>
public static class UiaCaptureService
{
    private static readonly AutomationProperty[] Properties =
    {
        AutomationElement.NameProperty,
        AutomationElement.HelpTextProperty,
        AutomationElement.ControlTypeProperty,
        AutomationElement.IsOffscreenProperty,
        AutomationElement.IsEnabledProperty,
        AutomationElement.IsKeyboardFocusableProperty,
        AutomationElement.IsPasswordProperty,
        AutomationElement.AutomationIdProperty,
        AutomationElement.HeadingLevelProperty,
        AutomationElement.LocalizedControlTypeProperty,
    };

    private static readonly AutomationPattern[] Patterns =
    {
        ValuePattern.Pattern,
        TogglePattern.Pattern,
        SelectionItemPattern.Pattern,
        RangeValuePattern.Pattern,
    };

    private const int MaxDocumentChars = 100_000;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    public static RawNode CaptureForegroundWindow()
    {
        var hwnd = GetForegroundWindow();
        return CaptureWindow(hwnd);
    }

    public static string GetWindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static RawNode CaptureWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("No foreground window to capture.");

        var request = new CacheRequest();
        foreach (var p in Properties) request.Add(p);
        foreach (var p in Patterns) request.Add(p);
        request.TreeScope = TreeScope.Subtree;
        request.Push();
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            return ReadElement(root);
        }
        finally
        {
            request.Pop();
        }
    }

    private static RawNode ReadElement(AutomationElement el)
    {
        var node = new RawNode
        {
            Name = Safe(() => el.Cached.Name) ?? "",
            HelpText = Safe(() => el.Cached.HelpText) ?? "",
            AutomationId = Safe(() => el.Cached.AutomationId) ?? "",
            ControlType = Safe(() => (el.Cached.ControlType.ProgrammaticName ?? "").Replace("ControlType.", "", StringComparison.OrdinalIgnoreCase)) ?? "",
            LocalizedControlType = Safe(() => el.Cached.LocalizedControlType) ?? "",
            IsOffscreen = Safe(() => el.Cached.IsOffscreen),
            IsEnabled = Safe(() => el.Cached.IsEnabled),
            IsKeyboardFocusable = Safe(() => el.Cached.IsKeyboardFocusable),
            IsPassword = Safe(() => el.Cached.IsPassword),
            RuntimeTarget = el,
        };

        var heading = Safe(() => el.GetCachedPropertyValue(AutomationElement.HeadingLevelProperty));
        if (heading is int hl && hl >= 80051 && hl <= 80059)
            node.HeadingLevel = hl - 80050;

        var value = Safe(() => (el.GetCachedPattern(ValuePattern.Pattern) as ValuePattern)?.Cached.Value);
        node.Value = value ?? "";

        if (node.ControlType == "Document" && !node.IsOffscreen && IsWritableDocument(el))
        {
            node.IsWritableDocument = true;
            if (node.Value.Length == 0)
                node.Value = ReadDocumentText(el);
        }

        var toggle = Safe(() => (el.GetCachedPattern(TogglePattern.Pattern) as TogglePattern)?.Cached.ToggleState.ToString());
        node.ToggleState = toggle ?? "";

        node.IsSelected = Safe(() =>
            (el.GetCachedPattern(SelectionItemPattern.Pattern) as SelectionItemPattern)?.Cached.IsSelected ?? false);

        var range = Safe(() => (el.GetCachedPattern(RangeValuePattern.Pattern) as RangeValuePattern)?.Cached.Value);
        if (range is double d && node.Value.Length == 0)
            node.Value = d.ToString("0.##");

        try
        {
            foreach (AutomationElement child in el.CachedChildren)
                node.Children.Add(ReadElement(child));
        }
        catch (ElementNotAvailableException)
        {
            // Element died mid-walk; keep what we have.
        }
        catch (InvalidOperationException)
        {
            // Cache was invalidated; keep what we have.
        }
        catch (COMException)
        {
            // COM call failed; keep what we have.
        }

        return node;
    }

    /// <summary>
    /// True for a document the user can type into (Notepad, Word, rich editors).
    /// A web page is also a Document, but reports itself read-only.
    /// </summary>
    public static bool IsWritableDocument(AutomationElement el)
    {
        try
        {
            if (el.Current.ControlType != ControlType.Document || !el.Current.IsEnabled || !el.Current.IsKeyboardFocusable)
                return false;
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
                return !((ValuePattern)value).Current.IsReadOnly;
            return el.TryGetCurrentPattern(TextPattern.Pattern, out var text)
                   && ((TextPattern)text).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is false;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadDocumentText(AutomationElement el)
    {
        try
        {
            return el.TryGetCurrentPattern(TextPattern.Pattern, out var text)
                ? ((TextPattern)text).DocumentRange.GetText(MaxDocumentChars) ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static T Safe<T>(Func<T> getter)
    {
        try { return getter(); }
        catch (ElementNotAvailableException) { return default!; }
        catch (InvalidOperationException) { return default!; }
        catch (COMException) { return default!; }
        catch (UnauthorizedAccessException) { return default!; }
    }
}

