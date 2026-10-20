using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace MicDeck.Services;

/// <summary>
/// Global hotkeys through RegisterHotKey. Per-sound bindings are re-registered
/// whenever the library or a gesture changes.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private readonly IntPtr _handle;
    private readonly HwndSource? _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0xA100;
    private bool _disposed;

    public HotkeyService(Window window)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(OnMessage);
    }

    public int RegisteredCount => _actions.Count;

    public void Clear()
    {
        foreach (var id in _actions.Keys.ToList())
        {
            UnregisterHotKey(_handle, id);
        }

        _actions.Clear();
    }

    /// <summary>Register a gesture such as "F8" or "Ctrl+Alt+1". False when taken.</summary>
    public bool TryRegister(string gesture, Action action)
    {
        if (!TryParseGesture(gesture, out var modifiers, out var virtualKey))
        {
            return false;
        }

        var id = _nextId++;

        if (!RegisterHotKey(_handle, id, modifiers | ModNoRepeat, virtualKey))
        {
            return false;
        }

        _actions[id] = action;
        return true;
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            try
            {
                action();
            }
            catch
            {
                // A hotkey handler must never take down the message loop.
            }
        }

        return IntPtr.Zero;
    }

    public static bool TryParseGesture(string? gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModWin;
                    break;
                default:
                    return false;
            }
        }

        var keyPart = parts[^1];
        Key key;

        if (keyPart.Length == 1 && char.IsDigit(keyPart[0]))
        {
            key = (Key)((int)Key.D0 + (keyPart[0] - '0'));
        }
        else if (keyPart.StartsWith("Num", StringComparison.OrdinalIgnoreCase) &&
                 keyPart.Length == 4 && char.IsDigit(keyPart[3]))
        {
            key = (Key)((int)Key.NumPad0 + (keyPart[3] - '0'));
        }
        else if (!Enum.TryParse(keyPart, ignoreCase: true, out key))
        {
            return false;
        }

        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            return false;
        }

        virtualKey = (uint)vk;
        return true;
    }

    public static string FormatGesture(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>();

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    public static string KeyName(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)key - (int)Key.D0).ToString();
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return "Num" + ((int)key - (int)Key.NumPad0);
        }

        return key switch
        {
            Key.Space => "Space",
            Key.Return => "Enter",
            Key.Escape => "Esc",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            _ => key.ToString(),
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
        _source?.RemoveHook(OnMessage);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
