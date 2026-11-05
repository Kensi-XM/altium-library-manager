using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using OriginalCircuit.AltiumSharp;
using OriginalCircuit.AltiumSharp.BasicTypes;
using OriginalCircuit.AltiumSharp.Records;

namespace Altium_Library_Manager.Services
{
    /// <summary>
    /// Corrections applied to libraries right after loading, for cases where AltiumSharp's renderer
    /// differs from Altium. The libraries are only read, never written back, so mutating them is safe.
    /// </summary>
    internal static class LibraryFixups
    {
        #region Symbol images

        /// <summary>
        /// Prepares image primitives of all symbols for rendering:
        ///  - images linked to an external file (not embedded) are loaded from disk; AltiumSharp leaves them null
        ///    and the renderer then throws;
        ///  - images without an alpha channel get their white background made transparent, as Altium shows them
        ///    (Altium embeds e.g. SVG logos as plain 24-bit bitmaps on white);
        ///  - images that can't be found are replaced with a transparent placeholder.
        /// </summary>
        public static void FixSymbolImages(SchLib lib, string libraryPath)
        {
            foreach (var component in lib.Items)
                foreach (var image in component.GetPrimitivesOfType<SchImage>(true))
                {
                    image.Image ??= LoadExternalImage(image.Filename, libraryPath);
                    image.Image = image.Image == null
                        ? new Bitmap(1, 1, PixelFormat.Format32bppArgb)
                        : Image.IsAlphaPixelFormat(image.Image.PixelFormat) ? image.Image : WithTransparentBackground(image.Image);
                }
        }

        private static Image? LoadExternalImage(string? fileName, string libraryPath)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return null;

            // The stored path is absolute; if the library was moved, look next to it and in an IMAGES folder
            var libraryDir = Path.GetDirectoryName(libraryPath)!;
            var name = Path.GetFileName(fileName);
            var candidates = new[]
            {
                fileName,
                Path.Combine(libraryDir, name),
                Path.Combine(libraryDir, "IMAGES", name),
                Path.Combine(libraryDir, "..", "IMAGES", name)
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    if (!File.Exists(candidate))
                        continue;

                    // Copy into memory so the file isn't kept locked for the lifetime of the image
                    using var stream = new MemoryStream(File.ReadAllBytes(candidate));
                    using var loaded = Image.FromStream(stream);
                    return new Bitmap(loaded);
                }
                catch (Exception)
                {
                    // Unsupported format (e.g. SVG) or unreadable file: try the next candidate
                }
            }
            return null;
        }

        /// <summary>
        /// Makes the near-white area connected to the image border transparent (flood fill from the edges),
        /// so white details inside the picture are kept.
        /// </summary>
        private static Bitmap WithTransparentBackground(Image source)
        {
            var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
                g.DrawImage(source, 0, 0, source.Width, source.Height);

            int w = bitmap.Width, h = bitmap.Height;
            var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                var pixels = new int[w * h];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

                static bool IsBackground(int argb) =>
                    ((argb >> 16) & 0xFF) >= 240 && ((argb >> 8) & 0xFF) >= 240 && (argb & 0xFF) >= 240;

                var queue = new Queue<int>();
                void Seed(int x, int y)
                {
                    var i = y * w + x;
                    if (IsBackground(pixels[i]) && pixels[i] != 0)
                    {
                        pixels[i] = 0;
                        queue.Enqueue(i);
                    }
                }

                for (var x = 0; x < w; x++) { Seed(x, 0); Seed(x, h - 1); }
                for (var y = 0; y < h; y++) { Seed(0, y); Seed(w - 1, y); }

                while (queue.Count > 0)
                {
                    var i = queue.Dequeue();
                    int x = i % w, y = i / w;
                    if (x > 0) Seed(x - 1, y);
                    if (x < w - 1) Seed(x + 1, y);
                    if (y > 0) Seed(x, y - 1);
                    if (y < h - 1) Seed(x, y + 1);
                }

                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }

        #endregion

        #region Pad solder mask

        /// <summary>
        /// Altium's IPC footprint wizard disables a pad's own solder mask opening by setting a huge negative
        /// manual expansion (e.g. -99999 mil) and adds separate solder mask regions instead. AltiumSharp adds the
        /// expansion to the pad size as is, producing a mask rectangle meters wide that covers the footprint and
        /// breaks auto-zoom. Such expansions are clamped so the opening shrinks to nothing, as in Altium.
        /// </summary>
        public static void FixPadMasks(PcbLib lib)
        {
            foreach (var component in lib.Items)
                foreach (var pad in component.GetPrimitivesOfType<PcbPad>(true))
                {
                    if (!pad.SolderMaskExpansionManual)
                        continue;

                    var smallest = Math.Min(
                        Math.Min(pad.SizeTop.X.ToMils(), pad.SizeTop.Y.ToMils()),
                        Math.Min(pad.SizeBottom.X.ToMils(), pad.SizeBottom.Y.ToMils()));
                    if (pad.SolderMaskExpansion.ToMils() < -smallest)
                        pad.SolderMaskExpansion = Coord.FromMils(-smallest);
                }
        }

        #endregion
    }
}
