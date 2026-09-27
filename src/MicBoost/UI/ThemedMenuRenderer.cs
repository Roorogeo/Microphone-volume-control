using MicBoost.Core;

namespace MicBoost.UI;

/// <summary>Context-menu renderer that follows the light/dark palette.</summary>
public sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly ThemePalette _p;

    public ThemedMenuRenderer(ThemePalette palette) : base(new PaletteColorTable(palette))
    {
        _p = palette;
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? _p.Text : _p.SubtleText;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = _p.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // Draw a simple check mark in the text colour instead of the default bitmap.
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = e.ImageRectangle;
        using var pen = new Pen(_p.Text, Math.Max(1.5f, r.Height / 9f));
        g.DrawLines(pen, new[]
        {
            new PointF(r.Left + r.Width * 0.20f, r.Top + r.Height * 0.52f),
            new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.74f),
            new PointF(r.Left + r.Width * 0.80f, r.Top + r.Height * 0.28f),
        });
    }

    private sealed class PaletteColorTable : ProfessionalColorTable
    {
        private readonly ThemePalette _p;
        public PaletteColorTable(ThemePalette p)
        {
            _p = p;
            UseSystemColors = false;
        }

        private Color Hover => _p.IsDark ? Color.FromArgb(62, 62, 62) : Color.FromArgb(229, 229, 229);

        public override Color ToolStripDropDownBackground => _p.Surface;
        public override Color ImageMarginGradientBegin => _p.Surface;
        public override Color ImageMarginGradientMiddle => _p.Surface;
        public override Color ImageMarginGradientEnd => _p.Surface;
        public override Color MenuBorder => _p.Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => _p.Border;
        public override Color SeparatorLight => _p.Surface;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
    }
}
