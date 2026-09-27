using System.Runtime.InteropServices;
using MicBoost.Core;

namespace MicBoost.UI;

/// <summary>Read-only text box that records a key combination when focused.</summary>
public sealed class HotkeyTextBox : TextBox
{
    private Keys _hotkey;

    public HotkeyTextBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        Cursor = Cursors.Hand;
    }

    public Keys Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value;
            Text = HotkeyFormat.Format(value);
        }
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Let Tab/Shift+Tab move focus normally; capture everything else.
        if (keyData is Keys.Tab or (Keys.Tab | Keys.Shift)) return base.ProcessCmdKey(ref msg, keyData);
        return false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;

        var key = e.KeyCode;
        if (key is Keys.Back or Keys.Delete && e.Modifiers == Keys.None)
        {
            Hotkey = Keys.None;
            return;
        }
        // Wait for a real key; modifiers alone do not make a hotkey.
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;

        Keys combo = key | e.Modifiers;
        if ((GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0)
            combo |= HotkeyFormat.WinModifier;
        Hotkey = combo;
    }

    protected override void OnKeyPress(KeyPressEventArgs e) => e.Handled = true;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
}
