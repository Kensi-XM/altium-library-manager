using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Altium_Library_Manager.Services;
using Altium_Library_Manager.Services.Settings;
using Microsoft.Web.WebView2.Core;

namespace Altium_Library_Manager
{
    /// <summary>
    /// Preview of the selected component: symbol (SchLib, with part selection), footprint (PcbLib) and its 3D model.
    /// Docked at the bottom of the main window.
    /// </summary>
    public partial class PreviewPanel : UserControl
    {
        private const int DebounceMs = 60;

        private const string ViewerHost = "viewer3d.local";
        private const string ModelsHost = "models.viewer3d.local";

        private CancellationTokenSource? _pending;
        private readonly StepModelStore _models = new();

        // Request received while the panel was collapsed; rendered when it is shown again
        private PreviewRequest? _deferred;

        // Resolved symbol of the component on screen, used to re-render it when another part is picked
        private string? _symbolPath;
        private string? _symbolRef;
        private bool _updatingParts;

        // 3D viewer: the page posts "ready" once OpenCascade is loaded; until then the last message is held
        private bool _viewerInitialized;
        private bool _viewerReady;
        private string? _pendingViewerMessage;
        private string? _shownModelKey;

        private readonly ComponentViewport _symbolView;
        private readonly ComponentViewport _footprintView;

        public PreviewPanel()
        {
            InitializeComponent();
            _symbolView = new ComponentViewport(SymbolHost, SymbolImage, () => RenderService);
            _footprintView = new ComponentViewport(FootprintHost, FootprintImage, () => RenderService);
            ThemeManager.EffectiveThemeChanged += (_, _) => ApplyTheme();
        }

        private AltiumRenderService _renderService = null!;

        public AltiumRenderService RenderService
        {
            get => _renderService;
            set
            {
                _renderService = value;
                ApplyTheme();
            }
        }

        /// <summary>
        /// Footprints get a dark background in the dark theme (like a PCB editor); symbols stay on a light sheet,
        /// as in Altium. The 3D viewer page recolors itself on a "theme" message.
        /// </summary>
        private void ApplyTheme()
        {
            if (_renderService == null)
                return;

            var dark = ThemeManager.IsDark;

            // Same color as the viewer's scene (viewer.js), also around the view inside its rounded frame
            var model = dark ? Color.FromRgb(0x25, 0x26, 0x29) : Color.FromRgb(0xF4, 0xF5, 0xF7);

            // The footprint shares the 3D view's background, so the two views look alike; in the dark theme only
            // when the dark footprint background is on (Options), otherwise it stays a white sheet
            var footprint = dark && !_options.DarkFootprintBackground ? Colors.White : model;
            FootprintHost.Background = new SolidColorBrush(footprint);
            _renderService.FootprintBackground = System.Drawing.Color.FromArgb(footprint.R, footprint.G, footprint.B);
            _footprintView.Refresh();

            ModelView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(model.R, model.G, model.B);
            ModelFrame.Background = new SolidColorBrush(model);
            if (_viewerReady)
                ModelView.CoreWebView2.PostWebMessageAsJson(ThemeMessage());
        }

        private static string ThemeMessage() => JsonSerializer.Serialize(new { type = "theme", dark = ThemeManager.IsDark });

        private PreviewOptions _options = new(true, true, true, ModelDetail.Normal, true);

        // The last component shown, shown again when the 3D view is switched on
        private PreviewRequest? _lastRequest;

        /// <summary>Preview options from Options → Appearance / Preview.</summary>
        public record PreviewOptions(bool DarkFootprintBackground, bool ShowBoard, bool ShowPads, ModelDetail ModelDetail,
            bool Show3D);

        public void ApplyOptions(PreviewOptions options)
        {
            var previous = _options;
            _options = options;
            if (options == previous)
                return;

            ApplyTheme();
            if (_viewerReady)
                ModelView.CoreWebView2.PostWebMessageAsJson(OptionsMessage());

            if (options.Show3D != previous.Show3D)
            {
                ModelCell.Visibility = options.Show3D ? Visibility.Visible : Visibility.Collapsed;
                _stacked = null;
                UpdateArrangement();
                if (options.Show3D)
                {
                    if (IsLoaded)
                        InitializeViewer();
                    if (_lastRequest != null)
                        ShowComponent(_lastRequest);
                }
            }
        }

        private string OptionsMessage() => JsonSerializer.Serialize(new
        {
            type = "options",
            showBoard = _options.ShowBoard,
            showPads = _options.ShowPads,
            // Tessellation tolerance relative to the model size: smaller = smoother but slower
            linearDeflection = _options.ModelDetail switch
            {
                ModelDetail.Low => 0.006,
                ModelDetail.High => 0.0008,
                _ => 0.002
            }
        });

        private void Panel_Loaded(object sender, RoutedEventArgs e)
        {
            // The browser engine (a separate process, ~100 MB) is only started when the 3D view is on
            if (_options.Show3D)
                InitializeViewer();
        }

        private async void InitializeViewer()
        {
            // Loaded fires again whenever the panel is re-attached to the tree
            if (_viewerInitialized)
                return;
            _viewerInitialized = true;

            try
            {
                // The third-party files are verified against audited hashes at build time (see the .csproj)
                var viewerFolder = Path.Combine(AppContext.BaseDirectory, "Viewer3D");
                var userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Altium Library Manager", "WebView2");
                var environment = await CoreWebView2Environment.CreateAsync(null, userData);
                await ModelView.EnsureCoreWebView2Async(environment);

                var web = ModelView.CoreWebView2;
                LockDown(web, environment);
                // WASM can't be loaded from a file:// page, so the folder is served through a virtual https host
                web.SetVirtualHostNameToFolderMapping(ViewerHost, viewerFolder, CoreWebView2HostResourceAccessKind.Deny);
                web.WebMessageReceived += (_, args) =>
                {
                    if (args.TryGetWebMessageAsString() != "ready")
                        return;
                    _viewerReady = true;
                    web.PostWebMessageAsJson(ThemeMessage());
                    web.PostWebMessageAsJson(OptionsMessage());
                    if (_pendingViewerMessage != null)
                        web.PostWebMessageAsJson(_pendingViewerMessage);
                    _pendingViewerMessage = null;
                };
                web.Navigate($"https://{ViewerHost}/viewer.html");
            }
            catch (Exception ex)
            {
                // Usually a missing WebView2 Runtime
                ModelView.Visibility = Visibility.Collapsed;
                ModelStatus.Text = $"3D unavailable: {ex.Message}";
            }
        }

        /// <summary>
        /// The viewer page only needs its own folder. Everything else is switched off or blocked here, independently
        /// of the page's Content-Security-Policy, so even compromised page code can't reach the network,
        /// navigate away, open windows, download files or use device permissions.
        /// </summary>
        private void LockDown(CoreWebView2 web, CoreWebView2Environment environment)
        {
            var settings = web.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;

            var origin = $"https://{ViewerHost}/";
            static bool IsLocal(string uri) =>
                uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase);

            // Models need their own host: WebView2 doesn't raise WebResourceRequested for a folder-mapped virtual host,
            // so requests there would never reach this handler
            var modelsPrefix = $"https://{ModelsHost}/";

            web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            web.WebResourceRequested += (_, e) =>
            {
                var uri = e.Request.Uri;
                if (uri.StartsWith(modelsPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    // STEP models are served straight from memory instead of being packed into JSON messages
                    var key = Uri.UnescapeDataString(uri[modelsPrefix.Length..]).Replace(".step", "", StringComparison.OrdinalIgnoreCase);
                    e.Response = _models.TryGet(key, out var data)
                        ? environment.CreateWebResourceResponse(new MemoryStream(data, writable: false), 200, "OK",
                            "Content-Type: text/plain; charset=iso-8859-1\r\nCache-Control: no-store\r\n" +
                            $"Access-Control-Allow-Origin: https://{ViewerHost}")
                        : environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                }
                else if (!uri.StartsWith(origin, StringComparison.OrdinalIgnoreCase) && !IsLocal(uri))
                {
                    e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                }
            };
            web.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith(origin, StringComparison.OrdinalIgnoreCase))
                    e.Cancel = true;
            };
            web.NewWindowRequested += (_, e) => e.Handled = true;
            web.DownloadStarting += (_, e) => e.Cancel = true;
            web.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        }

        private void Panel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible && _deferred != null)
            {
                var request = _deferred;
                _deferred = null;
                ShowComponent(request);
            }
        }

        private void PostToViewer(string json)
        {
            if (_viewerReady)
                ModelView.CoreWebView2.PostWebMessageAsJson(json);
            else
                _pendingViewerMessage = json;
        }

        #region Arrangement

        private bool? _stacked;

        private void CellsGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateArrangement();

        /// <summary>
        /// Side by side or stacked, whichever gives each of the views (two without 3D) the larger smaller dimension:
        /// a wide bottom slot keeps them in a row, a tall narrow right slot stacks them.
        /// </summary>
        private void UpdateArrangement()
        {
            double w = CellsGrid.ActualWidth, h = CellsGrid.ActualHeight;
            if (w <= 0 || h <= 0)
                return;

            var cells = new[] { SymbolCell, FootprintCell, ModelCell }.Where(c => c.Visibility == Visibility.Visible).ToList();
            var stacked = Math.Min(w, h / cells.Count) > Math.Min(w / cells.Count, h);
            if (stacked == _stacked)
                return;
            _stacked = stacked;

            // Views with a 6 px gap between them
            CellsGrid.ColumnDefinitions.Clear();
            CellsGrid.RowDefinitions.Clear();
            for (var i = 0; i < cells.Count; i++)
            {
                var sizes = i == 0 ? [new GridLength(1, GridUnitType.Star)] : new[] { new GridLength(6), new GridLength(1, GridUnitType.Star) };
                foreach (var size in sizes)
                {
                    if (stacked)
                        CellsGrid.RowDefinitions.Add(new RowDefinition { Height = size });
                    else
                        CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = size });
                }
                Grid.SetRow(cells[i], stacked ? i * 2 : 0);
                Grid.SetColumn(cells[i], stacked ? 0 : i * 2);
            }
        }

        #endregion

        /// <summary>Empties the preview, e.g. when the active tab has no selected row.</summary>
        public void Clear()
        {
            _pending?.Cancel();
            _deferred = null;
            _lastRequest = null;
            _pendingFootprint?.Cancel();
            _symbolPath = _symbolRef = null;
            SetParts(1);
            SetFootprints([], null);

            var none = new MeasureResult(null, null);
            Apply(_symbolView, SymbolCaption, SymbolSource, SymbolMessage, "Symbol", null, null, 1, none);
            Apply(_footprintView, FootprintCaption, FootprintSource, FootprintMessage, "Footprint", null, null, 1, none);
            Apply3D(null, new ModelResult(null, JsonSerializer.Serialize(new { type = "clear", text = "" }), ""));
        }

        /// <summary>
        /// Shows the symbol and the footprint. Rendering runs in the background; if a newer request arrives meanwhile
        /// (e.g. the user scrolls through rows with the arrow keys), the old one is cancelled and its result is dropped.
        /// While the panel is collapsed nothing is rendered: the last request waits until it is expanded.
        /// </summary>
        public async void ShowComponent(PreviewRequest request)
        {
            _pending?.Cancel();
            _pendingFootprint?.Cancel();
            if (!IsVisible)
            {
                _deferred = request;
                return;
            }

            _lastRequest = request;
            var cts = _pending = new CancellationTokenSource();
            var token = cts.Token;
            var show3D = _options.Show3D;

            // Cancellation (a newer request arrived, e.g. while flipping through rows) is checked with the flag rather
            // than OperationCanceledException: it happens constantly, and exceptions thrown inside Task.Run make the
            // debugger stop as "user-unhandled" even though nothing is wrong
            try
            {
                // Short delay: when flipping through rows quickly, only the row the user stops on is rendered
                await Task.Delay(DebounceMs);
                if (token.IsCancellationRequested)
                    return;

                var shownModelKey = _shownModelKey;
                // The main footprint first; the alternatives are rendered when picked
                var footprint = request.Footprints.FirstOrDefault() ?? new FootprintChoice(null, null);
                var result = await Task.Run(() =>
                {
                    var symbolPath = Resolve(request.SymbolPath, request.ResolvePath);
                    var parts = symbolPath == null ? 1 : SafePartCount(symbolPath, request.SymbolRef);
                    var s = Measure(symbolPath, request.SymbolRef, 1);
                    if (token.IsCancellationRequested)
                        return null;
                    var fp = RenderFootprint(footprint, request.ResolvePath, show3D, shownModelKey, token);
                    return fp == null ? null : new { symbolPath, parts, s, fp };
                });

                if (result == null || token.IsCancellationRequested)
                    return;
                // (Everything in the background part catches its own errors: an exception escaping this async void
                // method would end the application)

                _symbolPath = result.symbolPath;
                _symbolRef = request.SymbolRef;
                SetParts(result.parts);
                SetFootprints(request.Footprints, request.ResolvePath);
                Apply(_symbolView, SymbolCaption, SymbolSource, SymbolMessage, "Symbol", result.symbolPath, request.SymbolRef, 1, result.s);
                ApplyFootprint(footprint, result.fp);
            }
            finally
            {
                if (_pending == cts)
                    _pending = null;
                cts.Dispose();
            }
        }

        #region Symbol parts

        private void SetParts(int count)
        {
            _updatingParts = true;
            PartCombo.ItemsSource = count > 1 ? Enumerable.Range(1, count).Select(PartName).ToList() : null;
            PartCombo.SelectedIndex = count > 1 ? 0 : -1;
            PartCombo.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _updatingParts = false;
        }

        /// <summary>Altium names parts A, B, …, Z; numbers are used beyond that.</summary>
        private static string PartName(int part) => part <= 26 ? $"Part {(char)('A' + part - 1)}" : $"Part {part}";

        private async void PartCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingParts || PartCombo.SelectedIndex < 0 || _symbolPath == null)
                return;

            var part = PartCombo.SelectedIndex + 1;
            var path = _symbolPath;
            var name = _symbolRef;
            var result = await Task.Run(() => Measure(path, name, part));

            // Ignore the result if another component was selected meanwhile
            if (path == _symbolPath && name == _symbolRef && PartCombo.SelectedIndex + 1 == part)
                Apply(_symbolView, SymbolCaption, SymbolSource, SymbolMessage, "Symbol", path, name, part, result);
        }

        private int SafePartCount(string path, string? componentName)
        {
            try
            {
                return RenderService.GetPartCount(path, componentName);
            }
            catch (Exception)
            {
                // The render below reports the error
                return 1;
            }
        }

        #endregion

        #region Footprints

        // Footprints of the component on screen (the main one, then Footprint Ref 2, 3, …)
        private IReadOnlyList<FootprintChoice> _footprints = [];
        private Func<string, string?>? _resolvePath;
        private bool _updatingFootprints;

        /// <summary>A footprint's views: the path it resolved to, its bounds and (with the 3D view on) its model.</summary>
        private record FootprintResult(string? Path, MeasureResult Measure, ModelResult? Model);

        /// <summary>Runs on a background thread; null when cancelled.</summary>
        private FootprintResult? RenderFootprint(FootprintChoice footprint, Func<string, string?>? resolvePath,
            bool show3D, string? shownModelKey, CancellationToken token)
        {
            var path = Resolve(footprint.Path, resolvePath);
            var measure = Measure(path, footprint.Ref, 1);
            if (token.IsCancellationRequested)
                return null;
            var model = show3D ? Build3D(path, footprint.Ref, shownModelKey) : null;
            return new FootprintResult(path, measure, model);
        }

        private void ApplyFootprint(FootprintChoice footprint, FootprintResult result)
        {
            Apply(_footprintView, FootprintCaption, FootprintSource, FootprintMessage, "Footprint", result.Path, footprint.Ref, 1, result.Measure);
            if (result.Model != null)
                Apply3D(footprint.Ref, result.Model);
        }

        private void SetFootprints(IReadOnlyList<FootprintChoice> footprints, Func<string, string?>? resolvePath)
        {
            _footprints = footprints;
            _resolvePath = resolvePath;
            var many = footprints.Count > 1;

            _updatingFootprints = true;
            FootprintCombo.ItemsSource = many ? footprints.Select(f => FootprintName(f, footprints)).ToList() : null;
            FootprintCombo.SelectedIndex = many ? 0 : -1;
            FootprintCombo.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
            FootprintCombo.ToolTip = many ? $"{footprints.Count} footprints for this component" : null;
            _updatingFootprints = false;
        }

        /// <summary>The footprint's name, with its library when another footprint of the same name comes from another one.</summary>
        private static string FootprintName(FootprintChoice footprint, IReadOnlyList<FootprintChoice> all)
        {
            var name = string.IsNullOrWhiteSpace(footprint.Ref) ? "(no name)" : footprint.Ref;
            var sameName = all.Count(f => string.Equals(f.Ref, footprint.Ref, StringComparison.OrdinalIgnoreCase));
            return sameName > 1 && !string.IsNullOrWhiteSpace(footprint.Path)
                ? $"{name} ({Path.GetFileName(footprint.Path)})"
                : name;
        }

        private CancellationTokenSource? _pendingFootprint;

        private async void FootprintCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var index = FootprintCombo.SelectedIndex;
            if (_updatingFootprints || index < 0 || index >= _footprints.Count)
                return;

            _pendingFootprint?.Cancel();
            var cts = _pendingFootprint = new CancellationTokenSource();
            var footprints = _footprints;
            var footprint = footprints[index];
            var resolvePath = _resolvePath;
            var show3D = _options.Show3D;
            var shownModelKey = _shownModelKey;
            try
            {
                var result = await Task.Run(() => RenderFootprint(footprint, resolvePath, show3D, shownModelKey, cts.Token));

                // Ignore the result if another footprint or component was picked meanwhile
                if (result != null && !cts.IsCancellationRequested && footprints == _footprints &&
                    FootprintCombo.SelectedIndex == index)
                    ApplyFootprint(footprint, result);
            }
            finally
            {
                if (_pendingFootprint == cts)
                    _pendingFootprint = null;
                cts.Dispose();
            }
        }

        #endregion

        /// <summary><see cref="Error"/> is shown over the empty view instead of the component.</summary>
        private record MeasureResult(ViewBounds? Bounds, string? Error);

        private static string? Resolve(string? libraryPath, Func<string, string?>? resolvePath) =>
            string.IsNullOrWhiteSpace(libraryPath) ? null : resolvePath?.Invoke(libraryPath) ?? libraryPath;

        /// <summary>
        /// Runs on a background thread: loads the library and measures the component; the viewport then renders
        /// only the visible area at the size it is shown.
        /// </summary>
        private MeasureResult Measure(string? path, string? componentName, int part)
        {
            if (path == null)
                return new MeasureResult(null, "No library specified");

            try
            {
                var bounds = RenderService.GetContentBounds(path, componentName, part);
                return new MeasureResult(bounds, bounds == null ? "Component not found in this library" : null);
            }
            catch (Exception ex)
            {
                return new MeasureResult(null, $"Render error: {ex.Message}");
            }
        }

        private static void Apply(ComponentViewport view, TextBlock caption, TextBlock source, TextBlock message,
            string title, string? path, string? componentName, int part, MeasureResult result)
        {
            caption.Text = string.IsNullOrWhiteSpace(componentName) ? title : $"{title}: {componentName}";
            caption.ToolTip = componentName;
            source.Text = path == null ? null : Path.GetFileName(path);
            source.ToolTip = path;
            message.Text = result.Error;
            view.Show(path, componentName, part, result.Bounds);
        }

        /// <summary>
        /// Message for the viewer. <see cref="Json"/> == null means keep showing the model that is already on screen.
        /// </summary>
        private record ModelResult(string? Key, string? Json, string Status);

        /// <summary>
        /// Runs on a background thread. The message carries only pads and body placement; STEP models are registered
        /// in <see cref="_models"/> and the viewer downloads the ones it doesn't have yet.
        /// </summary>
        private ModelResult Build3D(string? path, string? componentName, string? shownKey)
        {
            if (path == null || AltiumRenderService.GetKind(path) != LibraryKind.PcbLib)
                return new ModelResult(null, Clear(""), "No footprint specified");

            // With the file time, so a footprint saved again in Altium is sent to the viewer again
            var key = $"{path}|{AltiumRenderService.Stamp(path)}|{componentName}".ToLowerInvariant();
            try
            {
                var footprint = RenderService.GetFootprint3D(path, componentName);
                if (footprint == null)
                    return new ModelResult(null, Clear("Component not found"), "Component not found");

                var status = !footprint.HasBodies ? "No 3D model, showing pads only"
                    : footprint.Bodies.Any(b => b.Step != null) ? "STEP model"
                    : "Simple Altium 3D body (no STEP)";

                foreach (var body in footprint.Bodies)
                    if (body.ModelKey != null && body.Step != null)
                        _models.Add(body.ModelKey, body.Step);

                // Neighbouring rows often reference the same footprint; then the viewer already shows it
                return key == shownKey
                    ? new ModelResult(key, null, status)
                    : new ModelResult(key, JsonSerializer.Serialize(new { type = "show", footprint }), status);
            }
            catch (Exception ex)
            {
                return new ModelResult(null, Clear("Error"), $"3D error: {ex.Message}");
            }

            static string Clear(string text) => JsonSerializer.Serialize(new { type = "clear", text });
        }

        private void Apply3D(string? componentName, ModelResult result)
        {
            ModelCaption.Text = string.IsNullOrWhiteSpace(componentName) ? "3D" : $"3D: {componentName}";
            // Without a viewer (WebView2 failed to start) its status keeps saying why
            if (ModelView.Visibility != Visibility.Visible)
                return;

            ModelStatus.Text = result.Status;

            if (result.Json != null)
                PostToViewer(result.Json);
            _shownModelKey = result.Key;
        }
    }

    /// <summary>
    /// What to show in the preview. <see cref="ResolvePath"/> turns a path from the database into an actual file path;
    /// it is called on a background thread because it may search subfolders.
    /// </summary>
    public record PreviewRequest(
        string? SymbolPath, string? SymbolRef,
        IReadOnlyList<FootprintChoice> Footprints,
        Func<string, string?>? ResolvePath = null);

    /// <summary>One of a component's footprints: its name in the library and the library path from the database.</summary>
    public record FootprintChoice(string? Ref, string? Path);
}
