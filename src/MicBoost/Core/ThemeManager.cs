using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MicBoost.Core;

/// <summary>Colours used by MicBoost's own windows.</summary>
public sealed record ThemePalette(
    bool IsDark,
    Color Background,
    Color Surface,
    Color Border,
    Color Text,
    Color SubtleText,
    Color Accent,
    Color Boost,
    Color Danger,
    Color Track,
    Color MeterGreen,
    Color MeterYellow);

/// <summary>
/// Follows the Windows light/dark setting (Settings > Personalization > Colors).
/// "Apps" mode drives window colours; "Windows" (system) mode drives the tray icon,
/// because the taskbar can be dark while apps are light.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static event EventHandler? ThemeChanged;

    public static ThemePalette Current { get; private set; } = Build();
    public static bool TaskbarIsDark { get; private set; } = ReadDword("SystemUsesLightTheme", 0) == 0;

    static ThemeManager()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color))
            return;

        var palette = Build();
        bool taskbarDark = ReadDword("SystemUsesLightTheme", 0) == 0;
        if (palette == Current && taskbarDark == TaskbarIsDark) return;

        Current = palette;
        TaskbarIsDark = taskbarDark;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static ThemePalette Build()
    {
        bool dark = ReadDword("AppsUseLightTheme", 1) == 0;
        Color accent = ReadAccentColor() ?? Color.FromArgb(0, 120, 212);

        return dark
            ? new ThemePalette(true,
                Background: Color.FromArgb(32, 32, 32),
                Surface: Color.FromArgb(45, 45, 45),
                Border: Color.FromArgb(70, 70, 70),
                Text: Color.FromArgb(243, 243, 243),
                SubtleText: Color.FromArgb(170, 170, 170),
                Accent: Lighten(accent, 0.25f),
                Boost: Color.FromArgb(255, 160, 50),
                Danger: Color.FromArgb(240, 80, 70),
                Track: Color.FromArgb(80, 80, 80),
                MeterGreen: Color.FromArgb(70, 200, 110),
                MeterYellow: Color.FromArgb(240, 200, 60))
            : new ThemePalette(false,
                Background: Color.FromArgb(249, 249, 249),
                Surface: Color.FromArgb(255, 255, 255),
                Border: Color.FromArgb(215, 215, 215),
                Text: Color.FromArgb(26, 26, 26),
                SubtleText: Color.FromArgb(96, 96, 96),
                Accent: accent,
                Boost: Color.FromArgb(215, 110, 0),
                Danger: Color.FromArgb(200, 40, 30),
                Track: Color.FromArgb(200, 200, 200),
                MeterGreen: Color.FromArgb(30, 160, 70),
                MeterYellow: Color.FromArgb(215, 165, 0));
    }

    private static int ReadDword(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(name) is int value ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>The user's accent colour (stored as ABGR in HKCU\Software\Microsoft\Windows\DWM).</summary>
    private static Color? ReadAccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
                return Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
        }
        catch
        {
            // fall through
        }
        return null;
    }

    private static Color Lighten(Color c, float amount) => Color.FromArgb(
        (int)(c.R + (255 - c.R) * amount), (int)(c.G + (255 - c.G) * amount), (int)(c.B + (255 - c.B) * amount));

    // --- Window chrome helpers ---------------------------------------------------------

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    /// <summary>Dark title bar for normal windows (Windows 10 20H1+ / 11).</summary>
    public static void ApplyTitleBar(Form form, bool dark)
    {
        if (!form.IsHandleCreated) return;
        int value = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    /// <summary>Rounded corners for borderless popups on Windows 11 (no-op on Windows 10).</summary>
    public static void ApplyRoundedCorners(Form form)
    {
        if (!form.IsHandleCreated) return;
        int value = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(form.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
    }

    /// <summary>Recursively colours standard WinForms controls for the current palette.</summary>
    public static void ApplyToControls(Control root, ThemePalette p)
    {
        root.BackColor = p.Background;
        root.ForeColor = p.Text;
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case Button b:
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = p.Border;
                    b.BackColor = p.Surface;
                    b.ForeColor = p.Text;
                    break;
                case TextBox or ComboBox or NumericUpDown:
                    c.BackColor = p.Surface;
                    c.ForeColor = p.Text;
                    break;
                case LinkLabel l:
                    l.LinkColor = p.Accent;
                    l.ActiveLinkColor = p.Accent;
                    l.BackColor = p.Background;
                    break;
                default:
                    c.BackColor = p.Background;
                    c.ForeColor = p.Text;
                    break;
            }
            // Recurse into layout containers, but not into composite inputs (their inner
            // edit/button children are handled by the parent control itself).
            if (c.HasChildren && c is not (NumericUpDown or ComboBox or TextBoxBase))
                ApplyToControls(c, p);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
