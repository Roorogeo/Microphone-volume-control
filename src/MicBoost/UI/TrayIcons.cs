using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using MicBoost.Core;

namespace MicBoost.UI;

public enum TrayIconKind
{
    Normal,
    Muted,
    Boosted,
    NoDevice,
}

/// <summary>
/// Draws the tray icons at runtime (no .ico resources needed) so they are crisp at the
/// current DPI and match the taskbar colour: a white microphone on a dark taskbar, a dark
/// one on a light taskbar, with a red slash when muted and an orange "+" badge when boosted.
/// </summary>
public sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<TrayIconKind, (Icon Icon, IntPtr Handle)> _icons = new();

    public TrayIcons() => Rebuild();

    public Icon Get(TrayIconKind kind) => _icons[kind].Icon;

    /// <summary>Re-creates all icons (call after theme or DPI changes).</summary>
    public void Rebuild()
    {
        // Build the new set before freeing the old one, so the tray never holds a dead handle.
        var old = _icons.Values.ToList();
        _icons.Clear();
        int size = GetSystemMetrics(SM_CXSMICON);
        if (size <= 0) size = 16;
        Color fg = ThemeManager.TaskbarIsDark ? Color.White : Color.FromArgb(28, 28, 28);

        foreach (TrayIconKind kind in Enum.GetValues(typeof(TrayIconKind)))
        {
            using var bmp = Draw(size, kind, fg);
            IntPtr hIcon = bmp.GetHicon();
            _icons[kind] = (Icon.FromHandle(hIcon), hIcon);
        }
        Release(old);
    }

    /// <summary>Also used for the app's window icon (settings form).</summary>
    public static Bitmap Draw(int size, TrayIconKind kind, Color fg)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size / 16f; // design grid is 16x16
        Color micColor = kind == TrayIconKind.NoDevice ? Color.FromArgb(140, fg) : fg;
        using var pen = new Pen(micColor, 1.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(micColor);

        // Capsule (the microphone head)
        using (var capsule = RoundedRect(new RectangleF(5.2f * s, 1f * s, 5.6f * s, 8.6f * s), 2.8f * s))
            g.FillPath(brush, capsule);

        // Holder arc, stem and base
        g.DrawArc(pen, 3f * s, 4.2f * s, 10f * s, 7.6f * s, 0, 180);
        g.DrawLine(pen, 8f * s, 11.8f * s, 8f * s, 14f * s);
        g.DrawLine(pen, 5.5f * s, 14.6f * s, 10.5f * s, 14.6f * s);

        switch (kind)
        {
            case TrayIconKind.Muted:
            {
                using var outline = new Pen(Color.Black, 3.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var slash = new Pen(Color.FromArgb(232, 60, 50), 2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(outline, 2.2f * s, 2.2f * s, 13.8f * s, 13.8f * s);
                g.DrawLine(slash, 2.2f * s, 2.2f * s, 13.8f * s, 13.8f * s);
                break;
            }
            case TrayIconKind.Boosted:
            {
                // Orange badge with a plus sign, bottom-right.
                var badge = new RectangleF(8.4f * s, 8.4f * s, 7.4f * s, 7.4f * s);
                using var badgeBrush = new SolidBrush(Color.FromArgb(255, 150, 30));
                using var plus = new Pen(Color.White, 1.5f * s);
                g.FillEllipse(badgeBrush, badge);
                float cx = badge.X + badge.Width / 2, cy = badge.Y + badge.Height / 2, r = 2.2f * s;
                g.DrawLine(plus, cx - r, cy, cx + r, cy);
                g.DrawLine(plus, cx, cy - r, cx, cy + r);
                break;
            }
        }
        return bmp;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void Release(IEnumerable<(Icon Icon, IntPtr Handle)> icons)
    {
        foreach (var (icon, handle) in icons)
        {
            icon.Dispose();          // Icon.FromHandle does not own the handle...
            DestroyIcon(handle);     // ...so free it explicitly.
        }
    }

    public void Dispose()
    {
        Release(_icons.Values.ToList());
        _icons.Clear();
    }

    private const int SM_CXSMICON = 49;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr hIcon);
}
