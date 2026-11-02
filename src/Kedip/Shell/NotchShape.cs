using System.Windows;
using System.Windows.Media;

namespace Kedip.Shell;

/// <summary>
/// Geometry of the island: flush with the top edge, rounded bottom corners, and concave "ears" at the two top corners
/// that curve outwards into the screen edge so the island melts into the bezel.
/// </summary>
public static class NotchShape
{
    /// <summary>Upper bound of the bottom-corner radius, so the large expanded island keeps a card-like silhouette.</summary>
    public const double MaxBottomRadius = 22;

    /// <param name="width">Body width (without ears).</param>
    /// <param name="height">Body height.</param>
    /// <param name="earRadius">Radius of the concave top-corner curves; the geometry is <c>width + 2·earRadius</c> wide.</param>
    public static Geometry Create(double width, double height, double earRadius) =>
        Create(width, height, earRadius, BottomRadius(width, height));

    /// <summary>Same as <see cref="Create(double, double, double)"/> with an explicit bottom-corner radius.</summary>
    public static Geometry Create(double width, double height, double earRadius, double bottomRadius)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        double ear = Math.Clamp(earRadius, 0, height);
        double radius = Math.Clamp(bottomRadius, 0, Math.Max(0, Math.Min(width / 2, height - ear)));
        double left = ear;
        double right = ear + width;

        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(right + ear, 0), isStroked: true, isSmoothJoin: false);
            if (ear > 0)
            {
                // Concave quarter circle centred at (right + ear, ear): from the screen edge down into the body side.
                ctx.ArcTo(new Point(right, ear), new Size(ear, ear), 0, false, SweepDirection.Counterclockwise, true, false);
            }

            ctx.LineTo(new Point(right, height - radius), true, false);
            if (radius > 0)
            {
                ctx.ArcTo(new Point(right - radius, height), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }

            ctx.LineTo(new Point(left + radius, height), true, false);
            if (radius > 0)
            {
                ctx.ArcTo(new Point(left, height - radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }

            ctx.LineTo(new Point(left, ear), true, false);
            if (ear > 0)
            {
                ctx.ArcTo(new Point(0, 0), new Size(ear, ear), 0, false, SweepDirection.Counterclockwise, true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>The body alone (square top, rounded bottom corners) at the origin; used to clip the island content.</summary>
    public static Geometry CreateBody(double width, double height) => Create(width, height, 0);

    /// <summary>Bottom-corner radius for a body size: ≈ 0.45 × height, never more than half the width or <see cref="MaxBottomRadius"/>.</summary>
    public static double BottomRadius(double width, double height) =>
        Math.Max(0, Math.Min(Math.Min(height * 0.45, width / 2), MaxBottomRadius));

    /// <summary>Ear radius for a collapsed notch height (≈ 8 DIP at the default 30 DIP height).</summary>
    public static double EarRadiusFor(double height) => Math.Clamp(Math.Round(height * 0.27), 6, 10);
}
