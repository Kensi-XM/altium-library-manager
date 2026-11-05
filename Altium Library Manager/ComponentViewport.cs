using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Altium_Library_Manager.Services;

namespace Altium_Library_Manager
{
    /// <summary>
    /// Zoomable, pannable 2D view of a symbol or footprint inside <paramref name="host"/>.
    /// Wheel zooms around the cursor, left drag pans, double-click fits the component.
    /// Only the visible area is rendered, at the host's physical pixel size, so the image stays sharp at any zoom.
    /// Renders run in the background and are coalesced: while one is running, further changes produce a single
    /// follow-up render with the latest view, so interaction never queues up work.
    /// </summary>
    internal sealed class ComponentViewport
    {
        private const double FitMargin = 0.92;
        private const double MinZoom = 0.25;
        private const double MaxZoom = 200;
        private const double WheelStep = 1.2;

        private readonly FrameworkElement _host;
        private readonly Image _image;
        private readonly Func<AltiumRenderService> _service;

        // What is shown; null path = nothing
        private string? _path;
        private string? _name;
        private int _part = 1;
        private ViewBounds? _content;

        // View: center in world units and zoom relative to "fit"
        private double _centerX, _centerY, _zoom = 1;

        private Point? _dragStart;
        private double _dragCenterX, _dragCenterY;

        private bool _rendering, _dirty;
        private int _generation;

        public ComponentViewport(FrameworkElement host, Image image, Func<AltiumRenderService> service)
        {
            _host = host;
            _image = image;
            _service = service;

            _image.Stretch = Stretch.Fill;
            _host.ClipToBounds = true;
            _host.SizeChanged += (_, _) => RequestRender();
            _host.MouseWheel += OnMouseWheel;
            _host.MouseLeftButtonDown += OnMouseDown;
            _host.MouseMove += OnMouseMove;
            _host.MouseLeftButtonUp += (_, _) => EndDrag();
            _host.LostMouseCapture += (_, _) => EndDrag();
        }

        /// <summary>Shows a component fitted to the view; <paramref name="content"/> null clears the view.</summary>
        public void Show(string? path, string? name, int part, ViewBounds? content)
        {
            _generation++;
            _path = content == null ? null : path;
            _name = name;
            _part = part;
            _content = content;
            Fit();
        }

        /// <summary>Renders the current view again (e.g. after the background color changed).</summary>
        public void Refresh() => RequestRender();

        public void Fit()
        {
            if (_content != null)
            {
                _centerX = _content.CenterX;
                _centerY = _content.CenterY;
            }
            _zoom = 1;
            RequestRender();
        }

        /// <summary>
        /// Device pixels per DIP of the host as it is on screen: the display scaling and also the window's own
        /// interface scale (a LayoutTransform), which would otherwise leave the image stretched and soft.
        /// </summary>
        private double PixelsPerDip
        {
            get
            {
                if (PresentationSource.FromVisual(_host) is not { RootVisual: { } root, CompositionTarget: { } target })
                    return VisualTreeHelper.GetDpi(_host).DpiScaleX;
                var layoutScale = _host.TransformToAncestor(root).TransformBounds(new Rect(0, 0, 1, 1)).Width;
                return layoutScale * target.TransformToDevice.M11;
            }
        }

        /// <summary>Scale in DIPs per world unit for the current zoom.</summary>
        private double DipsPerUnit()
        {
            if (_content == null || _host.ActualWidth < 1 || _host.ActualHeight < 1)
                return 0;
            var fit = Math.Min(_host.ActualWidth / _content.Width, _host.ActualHeight / _content.Height) * FitMargin;
            return fit * _zoom;
        }

        #region Mouse

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var scale = DipsPerUnit();
            if (scale <= 0)
                return;

            // Keep the point under the cursor fixed while zooming
            var p = e.GetPosition(_host);
            var dx = p.X - _host.ActualWidth / 2;
            var dy = p.Y - _host.ActualHeight / 2;
            var worldX = _centerX + dx / scale;
            var worldY = _centerY - dy / scale;

            _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? WheelStep : 1 / WheelStep), MinZoom, MaxZoom);
            var newScale = DipsPerUnit();
            _centerX = worldX - dx / newScale;
            _centerY = worldY + dy / newScale;

            e.Handled = true;
            RequestRender();
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_content == null)
                return;

            if (e.ClickCount == 2)
            {
                Fit();
                return;
            }

            _dragStart = e.GetPosition(_host);
            _dragCenterX = _centerX;
            _dragCenterY = _centerY;
            _host.CaptureMouse();
            _host.Cursor = Cursors.SizeAll;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragStart is not Point start)
                return;

            var scale = DipsPerUnit();
            if (scale <= 0)
                return;

            var p = e.GetPosition(_host);
            _centerX = _dragCenterX - (p.X - start.X) / scale;
            _centerY = _dragCenterY + (p.Y - start.Y) / scale;
            RequestRender();
        }

        private void EndDrag()
        {
            if (_dragStart == null)
                return;
            _dragStart = null;
            _host.Cursor = null;
            if (_host.IsMouseCaptured)
                _host.ReleaseMouseCapture();
        }

        #endregion

        private async void RequestRender()
        {
            if (_rendering)
            {
                _dirty = true;
                return;
            }

            _rendering = true;
            try
            {
                do
                {
                    _dirty = false;

                    var path = _path;
                    var dipsPerUnit = DipsPerUnit();
                    if (path == null || dipsPerUnit <= 0)
                    {
                        _image.Source = null;
                        continue;
                    }

                    // Render at physical pixels for a sharp image on scaled displays
                    var pixelsPerDip = PixelsPerDip;
                    var width = Math.Max((int)Math.Round(_host.ActualWidth * pixelsPerDip), 1);
                    var height = Math.Max((int)Math.Round(_host.ActualHeight * pixelsPerDip), 1);
                    var (name, part, cx, cy, generation) = (_name, _part, _centerX, _centerY, _generation);
                    // Only the fitted view is worth caching: it is shown again when the user comes back to the row
                    var isFitted = _zoom == 1 && _content != null && cx == _content.CenterX && cy == _content.CenterY;

                    var source = await Task.Run(() =>
                    {
                        try
                        {
                            return _service().RenderView(path, name, part, width, height, cx, cy, dipsPerUnit * pixelsPerDip, isFitted);
                        }
                        catch (Exception)
                        {
                            // Errors are reported when the component is shown; keep the previous image
                            return null;
                        }
                    });

                    if (generation == _generation && source != null)
                        _image.Source = source;
                }
                while (_dirty);
            }
            finally
            {
                _rendering = false;
            }
        }
    }
}
