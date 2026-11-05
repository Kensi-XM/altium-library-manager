using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using OriginalCircuit.AltiumSharp;
using OriginalCircuit.AltiumSharp.Drawing;
using OriginalCircuit.AltiumSharp.Records;

namespace Altium_Library_Manager.Services
{
    /// <summary>
    /// Renders a footprint with AltiumSharp, but draws the pad numbers itself. AltiumSharp sizes a number by the
    /// pad's long side, so on tall narrow pads (a QFN's bottom row) the numbers were far too large and ran into each
    /// other. Here each number fits inside its pad: horizontal as in Altium, along a tall pad only where horizontal
    /// text would be too small to read, never upside down, and left out when it would be too small anyway (as in
    /// Altium when zoomed out).
    /// (PcbLibRenderer is sealed, so this wraps its Render call instead of overriding it.)
    /// </summary>
    internal static class FootprintRenderer
    {
        // Share of the pad the number may fill, across and along the text
        private const float HeightFill = 0.62f, WidthFill = 0.85f;
        private const float MinFontPixels = 5;
        // Text too small horizontally runs along the pad only if that makes it at least this much larger
        private const float RotateGain = 1.4f;

        private static readonly Color TextColor = Color.FromArgb(240, 240, 240);

        public static void Render(PcbLibRenderer renderer, Graphics graphics, int width, int height, bool autoZoom)
        {
            if (renderer.Component is not PcbComponent component)
            {
                renderer.Render(graphics, width, height, autoZoom, false);
                return;
            }

            // Let AltiumSharp draw everything but the numbers (the designators are restored right after)
            var pads = component.GetPrimitivesOfType<PcbPad>(true).Where(p => !string.IsNullOrEmpty(p.Designator)).ToList();
            var designators = pads.Select(p => p.Designator).ToList();
            foreach (var pad in pads)
                pad.Designator = "";
            try
            {
                renderer.Render(graphics, width, height, autoZoom, false);
            }
            finally
            {
                for (var i = 0; i < pads.Count; i++)
                    pads[i].Designator = designators[i];
            }

            var state = graphics.Save();
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                using var brush = new SolidBrush(TextColor);
                using var format = new StringFormat(StringFormat.GenericTypographic)
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap
                };
                // One reference font for measuring all the numbers (a BGA has hundreds of pads)
                using var reference = new Font(FontFamily.GenericSansSerif, 100, GraphicsUnit.Pixel);
                foreach (var pad in pads)
                    DrawDesignator(renderer, graphics, pad, brush, format, reference);
            }
            finally
            {
                graphics.Restore(state);
            }
        }

        private static void DrawDesignator(PcbLibRenderer renderer, Graphics graphics, PcbPad pad, Brush brush, StringFormat format,
            Font reference)
        {
            var size = pad.SizeTop;
            float width = renderer.ScaleCoord(size.X), height = renderer.ScaleCoord(size.Y);
            if (width < MinFontPixels || height < MinFontPixels)
                return;

            // Text width per pixel of font size, from a measurement at the reference size (100 px)
            var perPixel = graphics.MeasureString(pad.Designator, reference, PointF.Empty, format).Width / 100;
            float FontFor(float along, float across) => Math.Min(across * HeightFill, along * WidthFill / perPixel);

            // Screen angles of the pad's own axes (the screen's y axis points down, so rotation is negated)
            var alongX = Upright(-pad.Rotation);
            var alongY = Upright(-pad.Rotation - 90);
            var (horizontal, other) = Math.Abs(alongX) <= Math.Abs(alongY)
                ? ((angle: alongX, font: FontFor(width, height)), (angle: alongY, font: FontFor(height, width)))
                : ((angle: alongY, font: FontFor(height, width)), (angle: alongX, font: FontFor(width, height)));
            // Horizontal, as in Altium; along the pad only where horizontal text would be too small to read
            var (textAngle, fontSize) = horizontal.font < MinFontPixels && other.font >= horizontal.font * RotateGain
                ? other
                : horizontal;
            if (fontSize < MinFontPixels)
                return;

            var center = renderer.ScreenFromWorld(pad.Location);
            using var font = new Font(FontFamily.GenericSansSerif, fontSize, GraphicsUnit.Pixel);
            var state = graphics.Save();
            graphics.TranslateTransform(center.X, center.Y);
            graphics.RotateTransform((float)textAngle);
            graphics.DrawString(pad.Designator, font, brush, PointF.Empty, format);
            graphics.Restore(state);
        }

        /// <summary>The same text direction, turned so the text reads left to right or bottom to top.</summary>
        private static double Upright(double degrees)
        {
            var angle = ((degrees % 360) + 360) % 360;   // 0…360
            if (angle > 180)
                angle -= 360;                             // -180…180
            if (angle > 90)
                angle -= 180;
            else if (angle <= -90)
                angle += 180;
            return angle;                                 // -90…90
        }
    }
}
