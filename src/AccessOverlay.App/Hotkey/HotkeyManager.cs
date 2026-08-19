using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace AccessOverlay.App.Hotkey;

/// <summary>
/// Global hotkey registration (RegisterHotKey). Default: Ctrl+Alt+A.
/// The same hotkey NVDA users already know for "move focus to system tray"-style gestures.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int ModAlt = 0x0001;
    private const int ModControl = 0x0002;
    private const int ModShift = 0x0004;
    private const int ModWin = 0x0008;

    private readonly HwndSource _source;
    private readonly int _id;

    public HotkeyManager(Window window, string hotkeySpec, int id = 0x0A0F)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle)!;
        _source.AddHook(WndProc);
        _id = id;

        (var modifiers, var key) = Parse(hotkeySpec);
        IsRegistered = RegisterHotKey(handle, _id, modifiers, key);
    }

    public bool IsRegistered { get; private set; }

    public event Action? HotkeyPressed;

    private static (uint Modifiers, uint Key) Parse(string spec)
    {
        uint mods = 0;
        uint key = 0;
        foreach (var part in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModControl; break;
                case "alt": mods |= ModAlt; break;
                case "shift": mods |= ModShift; break;
                case "win": mods |= ModWin; break;
                default:
                    if (part.Length == 1)
                        key = char.ToUpperInvariant(part[0]);
                    else if (Enum.TryParse(part, ignoreCase: true, out System.Windows.Input.Key parsed))
                        key = (uint)KeyInterop.VirtualKeyFromKey(parsed);
                    break;
            }
        }
        return (mods, key);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == _id)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        if (IsRegistered)
        {
            UnregisterHotKey(_source.Handle, _id);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
