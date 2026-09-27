using MicBoost.Audio;
using MicBoost.Core;

namespace MicBoost.UI;

/// <summary>
/// Small on-screen display shown when a hotkey changes the level or mute state. It never
/// takes focus, is click-through, and hides itself after a short delay.
/// </summary>
public sealed class OsdForm : Form
{
    private readonly System.Windows.Forms.Timer _hideTimer = new() { Interval = 1400 };
    private ThemePalette _palette = ThemeManager.Current;
    private VolumeState? _state;
    private int _layoutDpi;

    public OsdForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        Opacity = 0.94;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ThemeManager.ApplyRoundedCorners(this);
    }

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        Invalidate();
    }

    private int S(float v) => (int)Math.Round(v * DeviceDpi / 96f);

    /// <summary>Shows the OSD for the given state near the bottom centre of the active screen.</summary>
    public void ShowState(VolumeState state)
    {
        _state = state;
        if (!IsHandleCreated) CreateControl();

        var screen = Screen.FromPoint(Cursor.Position);
        if (_layoutDpi != DeviceDpi)
        {
            _layoutDpi = DeviceDpi;
            var old = Font;
            Font = new Font("Segoe UI Semibold", S(15), GraphicsUnit.Pixel);
            if (!ReferenceEquals(old, Control.DefaultFont)) old.Dispose();
        }
        Size = new Size(S(260), S(64));
        var wa = screen.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - S(72));

        Invalidate();
        if (!Visible) Show();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(_palette.Surface);
        using (var border = new Pen(_palette.Border))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        var s = _state;
        if (s == null) return;

        int pad = S(14);
        string title = !s.HasDevice ? "No microphone"
            : s.Muted ? "Microphone muted"
            : $"Microphone  {s.LevelPercent:0}%   {PopupForm.FormatDb(s.TotalDb)}";
        using (var text = new SolidBrush(s.Muted ? _palette.Danger : _palette.Text))
            g.DrawString(title, Font, text, pad, S(9));

        // Level bar: accent to 100 %, boost colour above.
        var bar = new Rectangle(pad, Height - S(20), Width - 2 * pad, S(6));
        using (var track = new SolidBrush(_palette.Track))
            g.FillRectangle(track, bar);
        if (s.HasDevice)
        {
            float fraction = (float)(s.LevelPercent / VolumeSlider.Maximum);
            int filled = (int)(bar.Width * fraction);
            int half = bar.Width / 2;
            using var accent = new SolidBrush(s.Muted ? _palette.SubtleText : _palette.Accent);
            using var boost = new SolidBrush(s.Muted ? _palette.SubtleText : _palette.Boost);
            g.FillRectangle(accent, bar.X, bar.Y, Math.Min(filled, half), bar.Height);
            if (filled > half) g.FillRectangle(boost, bar.X + half, bar.Y, filled - half, bar.Height);
            using var tick = new Pen(_palette.SubtleText);
            g.DrawLine(tick, bar.X + half, bar.Y - S(3), bar.X + half, bar.Bottom + S(3));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hideTimer.Dispose();
        base.Dispose(disposing);
    }
}
