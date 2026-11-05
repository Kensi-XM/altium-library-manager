using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Altium_Library_Manager
{
    /// <summary>
    /// A Border that clips its child to the rounded corners. A plain Border only rounds its own background and
    /// outline, so a child with a background (a header, a grid) would poke out square at the corners.
    /// Native child windows (WebView2) can't be clipped; give them some padding instead.
    /// </summary>
    public class ClippingBorder : Border
    {
        protected override Size ArrangeOverride(Size finalSize)
        {
            var size = base.ArrangeOverride(finalSize);
            if (Child is UIElement child)
            {
                // The child sits inside the outline, so its corners are smaller by the border width
                var radius = Math.Max(0, CornerRadius.TopLeft - Math.Max(BorderThickness.Left, BorderThickness.Top));
                child.Clip = radius > 0 ? new RectangleGeometry(new Rect(child.RenderSize), radius, radius) : null;
            }
            return size;
        }
    }
}
