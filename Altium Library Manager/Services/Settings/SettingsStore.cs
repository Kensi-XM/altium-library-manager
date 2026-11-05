using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace Altium_Library_Manager.Services.Settings
{
    /// <summary>
    /// Loads and saves <see cref="UserSettings"/> in %LOCALAPPDATA%\Altium Library Manager\settings.json.
    ///  - Writes are atomic (temp file + replace), so a crash mid-write never leaves a broken file.
    ///  - <see cref="RequestSave"/> coalesces frequent changes (dragging splitters, switching layouts) into one write.
    ///  - Files from older app versions are migrated by <see cref="Version"/>; an unreadable file is kept aside as
    ///    settings.json.bad and the defaults are used.
    /// </summary>
    public sealed class SettingsStore
    {
        public const int CurrentVersion = 4;

        // Layout presets by index (MainWindow.Layouts), now and in older versions
        private static readonly string[] LayoutOrder = ["Side preview", "Row", "Table", "Preview"];
        private static readonly string[] LayoutOrder3 = ["Preview", "Row", "Side preview", "Table"];
        private static readonly string[] LayoutOrderBefore3 = ["Table", "Row", "Preview", "Side preview"];

        private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            // So that a NaN or infinity can never stop a save or the copy for Options: Repair clears it when read back
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly string _path;
        private readonly DispatcherTimer _saveTimer;

        private SettingsStore(string path, UserSettings settings)
        {
            _path = path;
            Current = settings;
            _saveTimer = new DispatcherTimer { Interval = SaveDelay };
            _saveTimer.Tick += (_, _) => Save();
        }

        public static string DefaultPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Altium Library Manager", "settings.json");

        public UserSettings Current { get; }

        public static SettingsStore Load(string? path = null)
        {
            path ??= DefaultPath;
            return new SettingsStore(path, Read(path));
        }

        /// <summary>Deep copy, e.g. for an Options dialog that edits a copy and applies it on OK.</summary>
        public static UserSettings Clone(UserSettings settings) =>
            JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;

        /// <summary>Saves shortly after the last change; call after every user-visible settings change.</summary>
        public void RequestSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>Saves now (e.g. when the app closes), cancelling a pending delayed save.</summary>
        public void Save()
        {
            _saveTimer.Stop();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Current, JsonOptions));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Not being able to save settings must never break the app
                Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        private static UserSettings Read(string path)
        {
            if (!File.Exists(path))
                return new UserSettings();

            try
            {
                var json = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
                if (json == null)
                    return new UserSettings();

                var version = json["Version"]?.GetValue<int>() ?? 1;
                var settings = version < 2
                    ? MigrateFromVersion1(json)
                    : json.Deserialize<UserSettings>(JsonOptions) ?? new UserSettings();
                // The active layout preset is saved by index, and the presets were reordered in versions 3 and 4:
                // map the index through the preset's name
                if (version < CurrentVersion)
                {
                    var oldOrder = version < 3 ? LayoutOrderBefore3 : LayoutOrder3;
                    var preset = settings.Layout.ActivePreset;
                    if (preset >= 0 && preset < oldOrder.Length)
                        settings.Layout.ActivePreset = Array.IndexOf(LayoutOrder, oldOrder[preset]);
                }
                settings.Version = CurrentVersion;
                Repair(settings);
                return settings;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or IOException or ArgumentException or
                                           UnauthorizedAccessException or NotSupportedException)
            {
                Debug.WriteLine($"Settings file unreadable, using defaults: {ex.Message}");
                try
                {
                    File.Copy(path, path + ".bad", overwrite: true);
                }
                catch (Exception copy) when (copy is IOException or UnauthorizedAccessException)
                {
                    // Keeping a copy of the broken file is only a courtesy
                }
                return new UserSettings();
            }
        }

        private static double InRange(double value, double min, double max, double fallback) =>
            double.IsFinite(value) && value >= min && value <= max ? value : fallback;

        private static E Known<E>(E value, E fallback) where E : struct, Enum => Enum.IsDefined(value) ? value : fallback;

        /// <summary>
        /// A file can hold anything (edited by hand, written by another version): missing sections, numbers out of range,
        /// enum values that don't exist. Each such value goes back to its default here, so the rest of the app, and the
        /// Options sliders, only ever see what they could have set themselves. Other values are kept.
        /// </summary>
        public static void Repair(UserSettings settings)
        {
            var defaults = new UserSettings();
            settings.General ??= new GeneralSettings();
            settings.Window ??= new WindowSettings();
            settings.Appearance ??= new AppearanceSettings();
            settings.Tables ??= new TablesSettings();
            settings.Editing ??= new EditingSettings();
            settings.Preview ??= new PreviewSettings();
            settings.Templates ??= new TemplatesSettings();
            settings.Layout ??= new LayoutSettings();
            settings.Session ??= new SessionSettings();

            var window = settings.Window;
            if (new[] { window.Left, window.Top, window.Width, window.Height }.Any(v => v is { } size && (!double.IsFinite(size) || Math.Abs(size) > 100_000)) ||
                window.Width <= 0 || window.Height <= 0)
                (window.Left, window.Top, window.Width, window.Height) = (null, null, null, null);

            var appearance = settings.Appearance;
            appearance.Theme = Known(appearance.Theme, defaults.Appearance.Theme);
            appearance.UiScale = InRange(appearance.UiScale, 0.8, 1.5, defaults.Appearance.UiScale);

            var tables = settings.Tables;
            tables.Density = Known(tables.Density, defaults.Tables.Density);
            tables.ColumnSizing = Known(tables.ColumnSizing, defaults.Tables.ColumnSizing);
            tables.GridLines = Known(tables.GridLines, defaults.Tables.GridLines);
            tables.MinColumnWidth = InRange(tables.MinColumnWidth, 30, 200, defaults.Tables.MinColumnWidth);
            tables.MaxColumnWidth = InRange(tables.MaxColumnWidth, 120, 800, defaults.Tables.MaxColumnWidth);
            tables.FixedColumnWidth = InRange(tables.FixedColumnWidth, 60, 500, defaults.Tables.FixedColumnWidth);
            tables.DetailsFieldMaxShare = InRange(tables.DetailsFieldMaxShare, 0.2, 0.7, defaults.Tables.DetailsFieldMaxShare);
            tables.FontSize = InRange(tables.FontSize, 10, 16, TablesSettings.DefaultFontSize);
            if (tables.DetailsFieldWidth is { } field && InRange(field, 1, 5000, 0) == 0)
                tables.DetailsFieldWidth = null;
            tables.ColumnWidths ??= [];
            foreach (var (table, widths) in tables.ColumnWidths.ToList())
            {
                if (widths == null)
                    tables.ColumnWidths.Remove(table);
                else
                    foreach (var (column, width) in widths.ToList())
                        if (InRange(width, 1, 5000, 0) == 0)
                            widths.Remove(column);
            }
            tables.SearchColumns ??= [];
            foreach (var (table, columns) in tables.SearchColumns.ToList())
            {
                if (columns == null)
                    tables.SearchColumns.Remove(table);
                else
                    columns.RemoveAll(c => c == null);
            }

            var editing = settings.Editing;
            editing.UndoSteps = (int)InRange(editing.UndoSteps, EditingSettings.MinUndoSteps, EditingSettings.MaxUndoSteps, EditingSettings.MaxUndoSteps);

            settings.Preview.ModelDetail = Known(settings.Preview.ModelDetail, defaults.Preview.ModelDetail);
            settings.Templates.NewTableStart = Known(settings.Templates.NewTableStart, defaults.Templates.NewTableStart);

            var layout = settings.Layout;
            layout.ActivePreset = Math.Clamp(layout.ActivePreset, 0, LayoutOrder.Length - 1);
            layout.DatabasePanelWidth = InRange(layout.DatabasePanelWidth, 50, 5000, defaults.Layout.DatabasePanelWidth);
            layout.Presets ??= [];
            foreach (var (name, state) in layout.Presets.ToList())
                if (state == null || InRange(state.RightWidth, 0, 10_000, -1) < 0 || InRange(state.BottomHeight, 0, 10_000, -1) < 0)
                    layout.Presets.Remove(name);

            var session = settings.Session;
            session.OpenTables = session.OpenTables?.Where(t => t != null).ToList() ?? [];
            session.PinnedTables = session.PinnedTables?.Where(t => t != null).ToList() ?? [];
        }

        /// <summary>Version 1 was a flat object (no "Version"): move its values into the sections.</summary>
        private static UserSettings MigrateFromVersion1(JsonObject json)
        {
            T? Get<T>(string name) => json[name] is { } node ? node.Deserialize<T>(JsonOptions) : default;

            var settings = new UserSettings();
            settings.Layout.ActivePreset = Get<int>("Layout");
            settings.Layout.DatabasePanelWidth = Get<double?>("DatabasePanelWidth") ?? settings.Layout.DatabasePanelWidth;
            settings.Layout.DatabasePanelCollapsed = Get<bool>("DatabasePanelCollapsed");
            settings.Layout.Presets = Get<Dictionary<string, LayoutState>>("Layouts") ?? [];
            settings.Session.LastDatabase = Get<string>("LastDatabase");
            settings.Session.OpenTables = Get<List<string>>("OpenTables") ?? [];
            settings.Session.PinnedTables = Get<List<string>>("PinnedTables") ?? [];
            settings.Session.ActiveTable = Get<string>("ActiveTable");
            return settings;
        }
    }
}
