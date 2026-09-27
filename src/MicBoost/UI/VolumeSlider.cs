using System.Drawing.Drawing2D;
using MicBoost.Core;

namespace MicBoost.UI;

/// <summary>
/// Owner-drawn 0..200 % slider. The part above 100 % is drawn in the boost colour and a tick
/// marks 100 %, where the slider gently snaps while dragging. WinForms' TrackBar cannot be
/// themed for dark mode, hence the custom control.
/// </summary>
public sealed class VolumeSlider : Control
{
    public const double Maximum = 200;
    private const double SnapWindow = 2.0;

    private double _value = 100;
    private bool _dragging;
    private ThemePalette _palette = ThemeManager.Current;

    /// <summary>Raised when the user changes the value (mouse or keyboard), not when set from code.</summary>
    public event EventHandler? ValueChangedByUser;

    public VolumeSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.Slider;
        AccessibleName = "Microphone level";
        Cursor = Cursors.Hand;
    }

    public double Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, 0, Maximum);
            if (Math.Abs(v - _value) < 0.01) return;
            _value = v;
            AccessibleDescription = $"{v:0}%";
            Invalidate();
        }
    }

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        Invalidate();
    }

    private int Px(float v) => (int)Math.Round(v * DeviceDpi / 96f);
    private int ThumbRadius => Px(8);
    private Rectangle TrackBounds => new(ThumbRadius + 1, Height / 2 - Px(2), Width - 2 * (ThumbRadius + 1), Px(4));

    private float XForValue(double v) => TrackBounds.Left + (float)(v / Maximum) * TrackBounds.Width;

    private double ValueForX(int x)
    {
        var t = TrackBounds;
        double v = (x - t.Left) / (double)Math.Max(1, t.Width) * Maximum;
        v = Math.Round(Math.Clamp(v, 0, Maximum));
        // Magnetic 100 % so users can return to "no boost" precisely.
        if (Math.Abs(v - 100) <= SnapWindow) v = 100;
        return v;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? _palette.Background);

        var track = TrackBounds;
        float x100 = XForValue(100);
        float xVal = XForValue(_value);
        float radius = track.Height / 2f;

        using (var trackBrush = new SolidBrush(_palette.Track))
            FillRounded(g, trackBrush, track, radius);

        // Filled part: accent up to 100 %, boost colour beyond.
        using (var accent = new SolidBrush(Enabled ? _palette.Accent : _palette.Track))
            FillRounded(g, accent, new RectangleF(track.Left, track.Top, Math.Min(xVal, x100) - track.Left, track.Height), radius);
        if (_value > 100)
        {
            using var boost = new SolidBrush(_palette.Boost);
            g.FillRectangle(boost, new RectangleF(x100, track.Top, xVal - x100, track.Height));
        }

        // 100 % tick
        using (var tick = new Pen(_palette.SubtleText, Math.Max(1, Px(1))))
            g.DrawLine(tick, x100, track.Top - Px(5), x100, track.Bottom + Px(5));

        // Thumb
        int r = ThumbRadius;
        var thumb = new RectangleF(xVal - r, Height / 2f - r, 2 * r, 2 * r);
        using (var fill = new SolidBrush(_palette.Surface))
            g.FillEllipse(fill, thumb);
        using (var ring = new Pen(_value > 100 ? _palette.Boost : _palette.Accent, Px(2)))
            g.DrawEllipse(ring, thumb.X + 1, thumb.Y + 1, thumb.Width - 2, thumb.Height - 2);

        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(_palette.SubtleText) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
        }
    }

    private static void FillRounded(Graphics g, Brush brush, RectangleF r, float radius)
    {
        if (r.Width <= 0) return;
        using var path = new GraphicsPath();
        float d = Math.Min(radius * 2, r.Width);
        path.AddArc(r.X, r.Y, d, r.Height, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, r.Height, 270, 180);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private void SetFromUser(double v)
    {
        v = Math.Clamp(v, 0, Maximum);
        if (Math.Abs(v - _value) < 0.01) return;
        Value = v;
        ValueChangedByUser?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        _dragging = true;
        Capture = true;
        SetFromUser(ValueForX(e.X));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging && e.Button == MouseButtons.Left) SetFromUser(ValueForX(e.X));
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        // Capture is lost when a dialog pops up mid-drag or the popup hides.
        base.OnMouseCaptureChanged(e);
        _dragging = false;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double? next = e.KeyCode switch
        {
            Keys.Left or Keys.Down => _value - 1,
            Keys.Right or Keys.Up => _value + 1,
            Keys.PageDown => _value - 10,
            Keys.PageUp => _value + 10,
            Keys.Home => 0,
            Keys.End => Maximum,
            _ => null,
        };
        if (next.HasValue)
        {
            SetFromUser(Math.Round(next.Value));
            e.Handled = true;
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}
