using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Kedip.Shell.Settings;

/// <summary>
/// Dark, Windows 11-like renderer for the tray menu: rounded hover pills, thin separators, white check marks
/// and dots for radio items (items whose <see cref="ToolStripItem.Tag"/> is <see cref="RadioTag"/>).
/// </summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public const string RadioTag = "radio";

    public static readonly Color Background = Color.FromArgb(0x2B, 0x2B, 0x2D);
    public static readonly Color Text = Color.FromArgb(0xF5, 0xF5, 0xF7);
    private static readonly Color TextSecondary = Color.FromArgb(0xA1, 0xA1, 0xA6);
    private static readonly Color TextDisabled = Color.FromArgb(0x6E, 0x6E, 0x73);
    private static readonly Color Highlight = Color.FromArgb(0x3C, 0x3C, 0x3F);
    private static readonly Color Separator = Color.FromArgb(0x40, 0x40, 0x44);
    private static readonly Color Border = Color.FromArgb(0x48, 0x48, 0x4C);

    public DarkMenuRenderer()
        : base(new DarkColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (DwmStyling.HasRoundedCorners)
        {
            return; // DWM draws a rounded hairline border.
        }

        using var pen = new Pen(Border);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Enabled || !(e.Item.Selected || e.Item.Pressed))
        {
            return;
        }

        int inset = Scale(e.ToolStrip, 4);
        var rect = new Rectangle(inset, 0, e.Item.Width - 2 * inset, e.Item.Height);
        using var path = RoundedRect(rect, Scale(e.ToolStrip, 4));
        using var brush = new SolidBrush(Highlight);
        var g = e.Graphics;
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPath(brush, path);
        g.SmoothingMode = smoothing;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Text : TextDisabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled == false ? TextDisabled : TextSecondary;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int inset = Scale(e.ToolStrip, 12);
        int y = e.Item.Height / 2;
        using var pen = new Pen(Separator);
        e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        RectangleF r = e.ImageRectangle;
        if (r.Width <= 0 || r.Height <= 0)
        {
            return;
        }

        var g = e.Graphics;
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color color = e.Item.Enabled ? Text : TextDisabled;
        float size = Math.Min(r.Width, r.Height);
        float cx = r.X + r.Width / 2f;
        float cy = r.Y + r.Height / 2f;
        if (Equals(e.Item.Tag, RadioTag))
        {
            float d = size * 0.375f;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, cx - d / 2f, cy - d / 2f, d, d);
        }
        else
        {
            using var pen = new Pen(color, Math.Max(1.5f, size / 10f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            float s = size * 0.5f;
            g.DrawLines(pen,
            [
                new PointF(cx - s * 0.62f, cy + s * 0.02f),
                new PointF(cx - s * 0.2f, cy + s * 0.44f),
                new PointF(cx + s * 0.66f, cy - s * 0.46f),
            ]);
        }

        g.SmoothingMode = smoothing;
    }

    private static int Scale(ToolStrip? strip, int value) =>
        (int)Math.Round(value * (strip?.DeviceDpi ?? 96) / 96.0);

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public DarkColorTable() => UseSystemColors = false;

        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Highlight;
        public override Color MenuItemSelected => Highlight;
        public override Color MenuItemSelectedGradientBegin => Highlight;
        public override Color MenuItemSelectedGradientEnd => Highlight;
        public override Color MenuItemPressedGradientBegin => Highlight;
        public override Color MenuItemPressedGradientMiddle => Highlight;
        public override Color MenuItemPressedGradientEnd => Highlight;
        public override Color SeparatorDark => Separator;
        public override Color SeparatorLight => Separator;
        public override Color CheckBackground => Background;
        public override Color CheckSelectedBackground => Highlight;
        public override Color CheckPressedBackground => Highlight;
    }
}
