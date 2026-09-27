using MicBoost.Core;

namespace MicBoost.UI;

/// <summary>
/// Horizontal segmented input meter on a dB scale (-60..0 dBFS) with peak hold.
/// Segments are green up to -12 dBFS, yellow up to -3 dBFS, then red; the whole meter
/// turns red for a moment when the signal clips (peak at or above ~0 dBFS).
/// </summary>
public sealed class LevelMeter : Control
{
    private const float FloorDb = -60f;
    private const int Segments = 30;
    private const float ClipThreshold = 0.99f;
    private static readonly TimeSpan ClipHold = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan PeakHold = TimeSpan.FromMilliseconds(900);

    private float _level;      // smoothed display level (0..1 linear)
    private float _peakHold;   // linear
    private DateTime _peakHoldUntil;
    private DateTime _clipUntil;
    private ThemePalette _palette = ThemeManager.Current;

    public LevelMeter()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.ProgressBar;
        AccessibleName = "Input level";
    }

    public bool IsClipping => DateTime.UtcNow < _clipUntil;

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        Invalidate();
    }

    /// <summary>Feeds a new peak sample (linear, 0..1+). Call ~30 times per second.</summary>
    public void Push(float peak)
    {
        var now = DateTime.UtcNow;
        if (peak >= ClipThreshold) _clipUntil = now + ClipHold;

        // Instant attack, smooth release (~20 dB/s at 30 fps).
        _level = peak >= _level ? peak : _level * 0.89f;
        if (_level < 0.0005f) _level = 0;

        if (peak >= _peakHold || now > _peakHoldUntil)
        {
            _peakHold = peak;
            _peakHoldUntil = now + PeakHold;
        }
        Invalidate();
    }

    public void Reset()
    {
        _level = _peakHold = 0;
        _clipUntil = DateTime.MinValue;
        Invalidate();
    }

    private static float ToFraction(float linear)
    {
        if (linear <= 0) return 0;
        float db = 20f * MathF.Log10(linear);
        return Math.Clamp((db - FloorDb) / -FloorDb, 0f, 1f);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? _palette.Background);

        bool clipping = IsClipping;
        float fraction = ToFraction(_level);
        float gap = Math.Max(1f, DeviceDpi / 96f * 1.5f);
        float segWidth = (Width - gap * (Segments - 1)) / Segments;
        int lit = (int)MathF.Round(fraction * Segments);
        int holdSeg = (int)MathF.Round(ToFraction(_peakHold) * Segments) - 1;

        using var off = new SolidBrush(_palette.Track);
        using var green = new SolidBrush(_palette.MeterGreen);
        using var yellow = new SolidBrush(_palette.MeterYellow);
        using var red = new SolidBrush(_palette.Danger);

        for (int i = 0; i < Segments; i++)
        {
            float x = i * (segWidth + gap);
            float segDb = FloorDb + (i + 1) / (float)Segments * -FloorDb;
            Brush onBrush = clipping ? red : segDb > -3 ? red : segDb > -12 ? yellow : green;
            Brush b = i < lit || i == holdSeg ? onBrush : off;
            g.FillRectangle(b, x, 0, segWidth, Height);
        }
    }
}
