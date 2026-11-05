using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OriginalCircuit.AltiumSharp;
using OriginalCircuit.AltiumSharp.BasicTypes;
using OriginalCircuit.AltiumSharp.Drawing;
using OriginalCircuit.AltiumSharp.Records;
using DrawingBitmap = System.Drawing.Bitmap;

namespace Altium_Library_Manager.Services
{
    public enum LibraryKind
    {
        SchLib,
        PcbLib
    }

    /// <summary>
    /// Rectangle in AltiumSharp world units (internal Altium coordinates; the Y axis points up).
    /// </summary>
    public record ViewBounds(double CenterX, double CenterY, double Width, double Height);

    /// <summary>
    /// Loads SchLib/PcbLib files through AltiumSharp and renders components into WPF images.
    /// Loaded libraries are cached by path because database rows usually reference the same files,
    /// and rendered images go into a small LRU cache because neighbouring rows often share a symbol/footprint.
    /// Methods are thread-safe (called from background tasks); AltiumSharp isn't designed for concurrent access,
    /// so everything runs under a single lock.
    /// </summary>
    public class AltiumRenderService
    {
        // A fitted view of a large panel on a scaled display is several MB of pixels, so only a few are kept
        private const int ImageCacheCapacity = 12;
        private const long ImageCacheBytes = 32L << 20;
        // Browsing a database touches hundreds of libraries; only the recent ones are kept
        private const int LibraryCacheCapacity = 24;
        // A PcbLib keeps its embedded STEP models unpacked as .NET strings (2 bytes per char), so a single library
        // can take 15+ MB; without a size limit 24 heavy ones took over 400 MB
        private const long PcbLibCacheBytes = 96L << 20;

        private const int BoundsCacheCapacity = 4096;

        // Libraries are cached with the file's write time and reloaded when it changes, so a library edited and
        // saved in Altium shows its new version here. The time is also part of the bounds and image keys.
        private readonly object _sync = new();
        private readonly LruCache<string, (SchLib Lib, long Stamp)> _schLibs = new(LibraryCacheCapacity, StringComparer.OrdinalIgnoreCase);
        private readonly LruCache<string, (PcbLib Lib, long Stamp)> _pcbLibs =
            new(LibraryCacheCapacity, StringComparer.OrdinalIgnoreCase, entry => EstimateSize(entry.Lib), PcbLibCacheBytes);
        private readonly LruCache<string, BitmapSource?> _images =
            new(ImageCacheCapacity, sizeOf: image => image == null ? 0 : 4L * image.PixelWidth * image.PixelHeight,
                maxSize: ImageCacheBytes);
        private readonly LruCache<string, ViewBounds?> _bounds = new(BoundsCacheCapacity, StringComparer.OrdinalIgnoreCase);

        /// <summary>Last write time of the file (0 if it can't be read); changes when the library is saved.</summary>
        public static long Stamp(string path)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return 0;
            }
        }

        public static LibraryKind? GetKind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".schlib" => LibraryKind.SchLib,
            ".pcblib" => LibraryKind.PcbLib,
            _ => null
        };

        public IReadOnlyList<string> GetComponentNames(string path)
        {
            lock (_sync)
            {
                return GetKind(path) switch
                {
                    LibraryKind.SchLib => LoadSchLib(path).Items.Select(c => ((IComponent)c).Name).ToList(),
                    LibraryKind.PcbLib => LoadPcbLib(path).Items.Select(c => ((IComponent)c).Name).ToList(),
                    _ => throw new NotSupportedException($"Unsupported file type: {path}")
                };
            }
        }

        /// <summary>
        /// The components of a library named by a field, sorted, to pick from; or why they can't be listed. Reads the
        /// file, so it belongs on a background thread. Doesn't throw.
        /// </summary>
        public (List<string>? Names, string? Problem) ListLibrary(AccessDatabase database, string library, LibraryKind wanted)
        {
            try
            {
                if (database.ResolveLibraryPath(library) is not { } path)
                    return (null, $"The library wasn't found: {library}");
                if (GetKind(path) != wanted)
                    return (null, $"{Path.GetFileName(path)} isn't a {(wanted == LibraryKind.SchLib ? "schematic" : "PCB")} library.");
                return (GetComponentNames(path).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(), null);
            }
            catch (Exception ex)
            {
                return (null, $"Couldn't read {Path.GetFileName(library)}: {ex.Message}");
            }
        }

        /// <summary>
        /// Background of footprint renders: white in the light theme, dark gray in the dark one (like a PCB editor).
        /// Symbols always stay on a light "sheet", as in Altium. Part of the image cache key.
        /// </summary>
        public System.Drawing.Color FootprintBackground { get; set; } = System.Drawing.Color.White;

        /// <summary>
        /// Area actually covered by component <paramref name="componentName"/> of library <paramref name="path"/>,
        /// including pin names and other text. Null if the component isn't found.
        /// If the name is empty, the first component is used. For multi-part symbols <paramref name="part"/>
        /// selects the part (1 = Part A); it is ignored for footprints.
        /// </summary>
        public ViewBounds? GetContentBounds(string path, string? componentName, int part = 1)
        {
            var key = $"{path}|{Stamp(path)}|{componentName?.Trim()}|{part}".ToLowerInvariant();

            lock (_sync)
            {
                if (_bounds.TryGet(key, out var cached))
                    return cached;

                using var renderer = CreateRenderer(path, componentName, part);
                var bounds = renderer == null ? null : MeasureContent(renderer);
                _bounds.Add(key, bounds);
                return bounds;
            }
        }

        /// <summary>
        /// Renders a <paramref name="width"/> x <paramref name="height"/> pixel view of the component centered at
        /// (<paramref name="centerX"/>, <paramref name="centerY"/>) with <paramref name="pixelsPerUnit"/> pixels per
        /// world unit (see <see cref="ViewBounds"/>). Rendering only the visible area at the screen's pixel size keeps
        /// lines and text sharp at any zoom. Returns null if the component isn't found.
        /// Only views with <paramref name="cache"/> set are kept for reuse: fitted views come back when the user returns
        /// to a row, while every zoom/pan frame is unique and caching them only wasted memory (~280 MB per 64 frames).
        /// The result is frozen and can be passed between threads.
        /// </summary>
        public BitmapSource? RenderView(string path, string? componentName, int part,
            int width, int height, double centerX, double centerY, double pixelsPerUnit, bool cache)
        {
            var key = FormattableString.Invariant(
                $"{path}|{Stamp(path)}|{componentName?.Trim()}|{part}|{width}x{height}|{centerX:F0},{centerY:F0}|{pixelsPerUnit:G6}|{FootprintBackground.ToArgb()}").ToLowerInvariant();

            lock (_sync)
            {
                if (cache && _images.TryGet(key, out var cached))
                    return cached;

                using var renderer = CreateRenderer(path, componentName, part);
                BitmapSource? image = null;
                if (renderer != null)
                {
                    renderer.Center = new CoordPoint((int)Math.Round(centerX), (int)Math.Round(centerY));
                    renderer.Scale = pixelsPerUnit;
                    // The symbol's size, for RenderWithMargin (cached; the lock is re-entrant)
                    var content = renderer is SchLibRenderer ? GetContentBounds(path, componentName, part) : null;
                    var margin = content == null ? 0 : Math.Max(content.Width, content.Height);
                    using var bitmap = RenderBitmap(renderer, width, height, autoZoom: false, margin);
                    image = ToBitmapSource(bitmap);
                }
                if (cache)
                    _images.Add(key, image);
                return image;
            }
        }

        /// <summary>
        /// Number of parts of a symbol (Part A, Part B, …); 1 for single-part symbols, footprints and unknown components.
        /// </summary>
        public int GetPartCount(string path, string? componentName)
        {
            if (GetKind(path) != LibraryKind.SchLib)
                return 1;

            lock (_sync)
            {
                var component = FindComponent(LoadSchLib(path).Items, componentName);
                return Math.Max(component?.PartCount ?? 1, 1);
            }
        }

        /// <summary>
        /// 3D data of a footprint (pads and bodies with STEP models). Null if the component isn't found.
        /// </summary>
        public Footprint3D? GetFootprint3D(string path, string? componentName)
        {
            if (GetKind(path) != LibraryKind.PcbLib)
                return null;

            lock (_sync)
            {
                var component = FindComponent(LoadPcbLib(path).Items, componentName);
                return component == null ? null : Footprint3D.From(component);
            }
        }

        /// <param name="margin">World size of the area around the view that is rendered too (see RenderWithMargin).</param>
        private static DrawingBitmap RenderBitmap(Renderer renderer, int width, int height, bool autoZoom, double margin = 0)
        {
            // Not RenderAsImage: it creates a bitmap with the screen DPI, while AltiumSharp sets fonts in points
            // with factors tuned for 96 DPI. At 150% Windows scaling the text came out 1.5 times larger.
            var image = new DrawingBitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                image.SetResolution(96, 96);
                using var graphics = System.Drawing.Graphics.FromImage(image);
                if (renderer is PcbLibRenderer footprint)
                    FootprintRenderer.Render(footprint, graphics, width, height, autoZoom);
                else if (!autoZoom && margin > 0)
                    RenderWithMargin(renderer, graphics, width, height, margin);
                else
                    renderer.Render(graphics, width, height, autoZoom, false);
                return image;
            }
            catch
            {
                // A library that fails to draw must not leave the bitmap behind
                image.Dispose();
                throw;
            }
        }

        // Large enough for any symbol at the deepest zoom; GDI+ coordinates stay far from float precision limits
        private const double MaxMarginPixels = 200_000;

        /// <summary>
        /// AltiumSharp skips every primitive whose own bounds are off screen. A pin's bounds are its line only, so
        /// when zoomed in on a symbol the pin names (drawn inside the body, away from the line) vanished as soon as
        /// their lines left the view, and came back on zooming out. The symbol is therefore rendered for an area
        /// larger than the bitmap by the symbol's size on every side, so nothing in it is skipped; the bitmap clips
        /// what lies outside. (SchLibRenderer is sealed, so its visibility test can't be overridden.)
        /// </summary>
        private static void RenderWithMargin(Renderer renderer, System.Drawing.Graphics graphics, int width, int height, double margin)
        {
            var pixels = (int)Math.Ceiling(Math.Min(margin * renderer.Scale, MaxMarginPixels));
            graphics.SetClip(new System.Drawing.Rectangle(0, 0, width, height));
            graphics.TranslateTransform(-pixels, -pixels);
            renderer.Render(graphics, width + 2 * pixels, height + 2 * pixels, false, false);
        }

        /// <summary>
        /// AltiumSharp's auto-zoom fits only the geometry, so long pin names and other text stick out and get cut off.
        /// Here the component is drawn zoomed out on a transparent background and the bounds are taken from the pixels
        /// that were actually painted.
        /// </summary>
        private static ViewBounds MeasureContent(Renderer renderer)
        {
            const int size = 400;
            const double zoomOut = 0.35;

            // First pass: AltiumSharp's own fit gives the center and scale of the geometry
            RenderBitmap(renderer, size, size, autoZoom: true).Dispose();
            var geometry = new ViewBounds(renderer.Center.X, renderer.Center.Y, size / renderer.Scale, size / renderer.Scale);

            var background = renderer.BackgroundColor;
            renderer.BackgroundColor = System.Drawing.Color.Transparent;
            renderer.Scale *= zoomOut;
            try
            {
                using var bitmap = RenderBitmap(renderer, size, size, autoZoom: false);
                var painted = FindPaintedArea(bitmap);
                if (painted == null)
                    return geometry;

                var world = renderer.WorldFromScreen(painted.Value);
                return new ViewBounds(world.Center.X, world.Center.Y,
                    Math.Max(Math.Abs((double)(int)world.Width), 1), Math.Max(Math.Abs((double)(int)world.Height), 1));
            }
            finally
            {
                renderer.BackgroundColor = background;
            }
        }

        private static System.Drawing.RectangleF? FindPaintedArea(DrawingBitmap bitmap)
        {
            int w = bitmap.Width, h = bitmap.Height;
            var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var pixels = new int[w * h];
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    if ((pixels[y * w + x] >>> 24) == 0)
                        continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }

            return maxX < 0 ? null : new System.Drawing.RectangleF(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        private Renderer? CreateRenderer(string path, string? componentName, int part)
        {
            switch (GetKind(path))
            {
                case LibraryKind.SchLib:
                {
                    var lib = LoadSchLib(path);
                    var component = FindComponent(lib.Items, componentName);
                    if (component == null)
                        return null;

                    // The renderer draws only the primitives of the current part. The file stores whichever part
                    // was last open in Altium, so it is always set explicitly (safe: we are under the lock)
                    component.CurrentPartId = Math.Clamp(part, 1, Math.Max(component.PartCount, 1));
                    return new SchLibRenderer(lib.Header, lib) { Component = component };
                }
                case LibraryKind.PcbLib:
                {
                    var lib = LoadPcbLib(path);
                    var component = FindComponent(lib.Items, componentName);
                    return component == null ? null : new PcbLibRenderer { Component = component, BackgroundColor = FootprintBackground };
                }
                default:
                    throw new NotSupportedException($"Unsupported file type: {path}");
            }
        }

        private static T? FindComponent<T>(List<T> items, string? name) where T : class, IComponent
        {
            if (string.IsNullOrWhiteSpace(name))
                return items.FirstOrDefault();

            // Exact name first: some components really have leading/trailing spaces (" STM32G031KxU UFQFPN32"),
            // then ignoring them, as database fields are often trimmed
            return items.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? items.FirstOrDefault(c => string.Equals(c.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private SchLib LoadSchLib(string path)
        {
            var stamp = Stamp(path);
            if (_schLibs.TryGet(path, out var cached) && cached.Stamp == stamp)
                return cached.Lib;

            using var reader = new SchLibReader();
            var lib = reader.Read(path);
            LibraryFixups.FixSymbolImages(lib, path);
            _schLibs.Add(path, (lib, stamp));
            return lib;
        }

        /// <summary>
        /// Rough memory footprint of a loaded PcbLib: dominated by the STEP text of its 3D models; primitives are
        /// counted as a flat allowance.
        /// </summary>
        private static long EstimateSize(PcbLib lib) =>
            (512L << 10) + lib.Items
                .SelectMany(c => c.GetPrimitivesOfType<PcbComponentBody>(true))
                .Sum(b => 2L * (b.StepModel?.Length ?? 0));

        private PcbLib LoadPcbLib(string path)
        {
            var stamp = Stamp(path);
            if (_pcbLibs.TryGet(path, out var cached) && cached.Stamp == stamp)
                return cached.Lib;

            var lib = PcbLibCompat.Read(path);
            LibraryFixups.FixPadMasks(lib);
            _pcbLibs.Add(path, (lib, stamp));
            return lib;
        }

        /// <summary>
        /// Copies the pixels directly, without encoding to PNG in between.
        /// </summary>
        private static BitmapSource ToBitmapSource(DrawingBitmap image)
        {
            var rect = new System.Drawing.Rectangle(0, 0, image.Width, image.Height);
            var data = image.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, image.PixelFormat);
            try
            {
                var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null,
                    data.Scan0, data.Stride * image.Height, data.Stride);
                bitmap.Freeze();
                return bitmap;
            }
            finally
            {
                image.UnlockBits(data);
            }
        }
    }
}
