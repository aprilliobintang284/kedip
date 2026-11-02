using System.Windows;
using System.Windows.Media;

namespace Kedip.Shell;

/// <summary>
/// Geometry of the island: flush with the top edge, rounded bottom corners, inverse "ears" at the top corners.
/// OWNER: A. Scaffold stub — replace the implementation, keep the public API.
/// </summary>
public static class NotchShape
{
    /// <param name="width">Body width (without ears).</param>
    /// <param name="height">Body height.</param>
    /// <param name="earRadius">Radius of the concave top-corner curves; the geometry is <c>width + 2·earRadius</c> wide.</param>
    public static Geometry Create(double width, double height, double earRadius) =>
        new RectangleGeometry(new Rect(earRadius, 0, width, height), height * 0.45, height * 0.45);
}
