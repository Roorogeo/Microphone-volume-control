using System.Runtime.InteropServices;

namespace MicBoost.Core;

public enum HotkeyAction
{
    VolumeUp = 1,
    VolumeDown = 2,
    ToggleMute = 3,
}

/// <summary>
/// System-wide hotkeys via RegisterHotKey. A hidden message-only window receives WM_HOTKEY,
/// so no polling or keyboard hook is involved (zero idle cost).
/// </summary>
public sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly HashSet<int> _registered = new();

    public event EventHandler<HotkeyAction>? Pressed;

    public HotkeyManager()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE, Caption = "MicBoostHotkeys" });
    }

    /// <summary>Registers a hotkey. Returns false if the combination is invalid or taken by another app.</summary>
    public bool Register(HotkeyAction action, Keys keys)
    {
        Unregister(action);
        Keys keyCode = keys & Keys.KeyCode;
        if (keyCode == Keys.None || keyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu) return false;

        uint mods = 0;
        if (keys.HasFlag(Keys.Control)) mods |= MOD_CONTROL;
        if (keys.HasFlag(Keys.Alt)) mods |= MOD_ALT;
        if (keys.HasFlag(Keys.Shift)) mods |= MOD_SHIFT;
        if ((keys & HotkeyFormat.WinModifier) != 0) mods |= MOD_WIN;
        // Volume keys may auto-repeat while held; mute must not toggle repeatedly.
        if (action == HotkeyAction.ToggleMute) mods |= MOD_NOREPEAT;

        if (!RegisterHotKey(Handle, (int)action, mods, (uint)keyCode))
        {
            Log.Warn($"RegisterHotKey failed for {action} ({HotkeyFormat.Format(keys)}): error {Marshal.GetLastWin32Error()}");
            return false;
        }
        _registered.Add((int)action);
        return true;
    }

    public void Unregister(HotkeyAction action)
    {
        if (_registered.Remove((int)action))
            UnregisterHotKey(Handle, (int)action);
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.ToArray())
            Unregister((HotkeyAction)id);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && Enum.IsDefined(typeof(HotkeyAction), (int)m.WParam))
        {
            Pressed?.Invoke(this, (HotkeyAction)(int)m.WParam);
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>
/// Converts between <see cref="Keys"/> and the human-readable form stored in settings
/// ("Ctrl+Alt+Up"). WinForms has no Win modifier flag, so an unused high bit is borrowed.
/// </summary>
public static class HotkeyFormat
{
    public const Keys WinModifier = (Keys)0x00080000;

    public static string Format(Keys keys)
    {
        if ((keys & Keys.KeyCode) == Keys.None) return "None";
        var parts = new List<string>();
        if (keys.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (keys.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (keys.HasFlag(Keys.Shift)) parts.Add("Shift");
        if ((keys & WinModifier) != 0) parts.Add("Win");
        parts.Add(KeyName(keys & Keys.KeyCode));
        return string.Join("+", parts);
    }

    public static Keys Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Equals("None", StringComparison.OrdinalIgnoreCase))
            return Keys.None;

        Keys result = Keys.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": result |= Keys.Control; break;
                case "alt": result |= Keys.Alt; break;
                case "shift": result |= Keys.Shift; break;
                case "win": case "windows": result |= WinModifier; break;
                default:
                    if (raw.Length == 1 && char.IsDigit(raw[0]))
                        result |= Keys.D0 + (raw[0] - '0');
                    else if (Enum.TryParse(raw, ignoreCase: true, out Keys key))
                        result |= key & Keys.KeyCode;
                    else
                        return Keys.None;
                    break;
            }
        }
        return result;
    }

    private static string KeyName(Keys key) => key switch
    {
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (key - Keys.D0))).ToString(),
        Keys.Next => "PageDown",
        Keys.Prior => "PageUp",
        _ => key.ToString(),
    };
}
