namespace Altium_Library_Manager.Services.Settings
{
    /// <summary>
    /// All user settings, grouped by area. Persisted as JSON by <see cref="SettingsStore"/>;
    /// <see cref="Version"/> lets the store migrate files written by older versions of the app.
    /// </summary>
    public sealed class UserSettings
    {
        public int Version { get; set; } = SettingsStore.CurrentVersion;

        public GeneralSettings General { get; set; } = new();

        public WindowSettings Window { get; set; } = new();

        public AppearanceSettings Appearance { get; set; } = new();

        public TablesSettings Tables { get; set; } = new();

        public EditingSettings Editing { get; set; } = new();

        public PreviewSettings Preview { get; set; } = new();

        public TemplatesSettings Templates { get; set; } = new();

        public LayoutSettings Layout { get; set; } = new();

        public SessionSettings Session { get; set; } = new();
    }

    /// <summary>Main window position and size (in DIPs), restored at startup.</summary>
    public sealed class WindowSettings
    {
        public double? Left { get; set; }
        public double? Top { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public bool Maximized { get; set; }
    }

    public enum AppTheme
    {
        /// <summary>Follow the Windows "app mode" setting.</summary>
        System,
        Light,
        Dark
    }

    public sealed class GeneralSettings
    {
        /// <summary>Reopen the last database at startup.</summary>
        public bool RestoreSession { get; set; } = true;

        /// <summary>With <see cref="RestoreSession"/>: also reopen the tables that were open.</summary>
        public bool RestoreTabs { get; set; } = true;

        /// <summary>A click in the table list opens a preview tab (replaced by the next click), as in VS Code;
        /// otherwise every table opens in a tab of its own.</summary>
        public bool PreviewTabs { get; set; } = true;
    }

    public sealed class AppearanceSettings
    {
        public AppTheme Theme { get; set; } = AppTheme.System;

        /// <summary>Scale of the whole window content (1 = 100%), independent of the Windows display scaling.</summary>
        public double UiScale { get; set; } = 1.0;

        /// <summary>In the dark theme, draw footprints on a dark background like a PCB editor (otherwise white).</summary>
        public bool DarkFootprintBackground { get; set; } = true;
    }

    public enum TableDensity
    {
        Compact,
        Normal,
        Comfortable
    }

    public sealed class TablesSettings
    {
        public TableDensity Density { get; set; } = TableDensity.Compact;

        /// <summary>How a table's columns get their width when it is opened.</summary>
        public ColumnSizing ColumnSizing { get; set; } = ColumnSizing.FitContent;

        /// <summary>With <see cref="ColumnSizing.FitContent"/>: no column narrower than this (in DIPs).</summary>
        public double MinColumnWidth { get; set; } = 40;

        /// <summary>With <see cref="ColumnSizing.FitContent"/>: longer values (URLs, paths) are trimmed to this
        /// width (in DIPs).</summary>
        public double MaxColumnWidth { get; set; } = 300;

        /// <summary>With <see cref="ColumnSizing.Fixed"/>: the width of every column (in DIPs).</summary>
        public double FixedColumnWidth { get; set; } = 150;

        /// <summary>Widths set by dragging a column edge are kept for the next time the table is opened.</summary>
        public bool RememberColumnWidths { get; set; } = true;

        /// <summary>Remembered widths (in DIPs): table name → column name → width.</summary>
        public Dictionary<string, Dictionary<string, double>> ColumnWidths { get; set; } = [];

        /// <summary>The columns the row search looks in, per table (table name → column names); a table not listed
        /// is searched in all its columns.</summary>
        public Dictionary<string, List<string>> SearchColumns { get; set; } = [];

        /// <summary>The Details panel's Field column fits the field names, but takes at most this share of the
        /// panel width (0.2–0.7), unless its width was set by dragging (<see cref="DetailsFieldWidth"/>).</summary>
        public double DetailsFieldMaxShare { get; set; } = 0.45;

        /// <summary>Width of the Details Field column set by dragging its edge (in DIPs); null = fit the names.</summary>
        public double? DetailsFieldWidth { get; set; }

        public GridLines GridLines { get; set; } = GridLines.All;

        /// <summary>Every other row slightly shaded, for following long rows.</summary>
        public bool AlternatingRows { get; set; }

        /// <summary>Keep the first column (Part Number) in place while scrolling sideways.</summary>
        public bool FreezeFirstColumn { get; set; } = true;

        /// <summary>Size of the text in the tables (in DIPs, 10–16); rows grow with it when needed.</summary>
        public double FontSize { get; set; } = DefaultFontSize;

        public const double DefaultFontSize = 13;

        /// <summary>The row of the selected cell is shaded, so it can be followed across a wide table.</summary>
        public bool HighlightCurrentRow { get; set; } = true;
    }

    /// <summary>How rows are changed in a table tab.</summary>
    public sealed class EditingSettings
    {
        /// <summary>Edit is on in a tab that opens to stay (a temporary tab stays read-only).</summary>
        public bool EditOnOpen { get; set; }

        /// <summary>Ask before deleting rows. Off, Delete acts at once, since Ctrl+Z brings them back.</summary>
        public bool ConfirmDelete { get; set; }

        /// <summary>How many changes each table remembers for Ctrl+Z (50–500).</summary>
        public int UndoSteps { get; set; } = MaxUndoSteps;

        public const int MinUndoSteps = 50, MaxUndoSteps = 500;
    }

    public enum ColumnSizing
    {
        /// <summary>Fit the header and the values, between the minimum and maximum width.</summary>
        FitContent,
        /// <summary>The same width for every column.</summary>
        Fixed
    }

    public enum GridLines
    {
        None,
        Horizontal,
        All
    }

    public enum ModelDetail
    {
        Low,
        Normal,
        High
    }

    public sealed class PreviewSettings
    {
        /// <summary>The 3D view. Off, its browser engine isn't even started, which saves memory and startup time.</summary>
        public bool Show3D { get; set; } = true;

        /// <summary>Show a board under the 3D model.</summary>
        public bool ShowBoard { get; set; } = true;

        /// <summary>Show the footprint's copper pads in 3D.</summary>
        public bool ShowPads { get; set; } = true;

        /// <summary>STEP tessellation detail: higher is smoother but slower for large models.</summary>
        public ModelDetail ModelDetail { get; set; } = ModelDetail.Normal;
    }

    /// <summary>What the New table dialog starts with.</summary>
    public enum NewTableStart
    {
        /// <summary>Whatever was used for the previous table.</summary>
        LastUsed,
        /// <summary>Part Number, symbol and footprint.</summary>
        Minimal,
        /// <summary>Only Part Number.</summary>
        Empty,
        /// <summary>The template named <see cref="TemplatesSettings.StartTemplate"/>.</summary>
        Template
    }

    public sealed class TemplatesSettings
    {
        /// <summary>Folder of the template files; null = %LOCALAPPDATA%\Altium Library Manager\Templates. A shared
        /// folder (network drive, synced or versioned folder) gives a team the same templates.</summary>
        public string? Folder { get; set; }

        public NewTableStart NewTableStart { get; set; } = NewTableStart.LastUsed;

        /// <summary>With <see cref="NewTableStart.Template"/>: the template's name.</summary>
        public string? StartTemplate { get; set; }

        /// <summary>Open a table in a tab right after creating it.</summary>
        public bool OpenCreatedTable { get; set; } = true;

        /// <summary>Folder last used to import or export templates (the file dialogs start there).</summary>
        public string? LastExchangeFolder { get; set; }
    }

    public sealed class LayoutSettings
    {
        /// <summary>Index of the active layout preset (see MainWindow.Layouts).</summary>
        public int ActivePreset { get; set; }

        /// <summary>Width of the database panel on the left (in DIPs).</summary>
        public double DatabasePanelWidth { get; set; } = 260;

        public bool DatabasePanelCollapsed { get; set; }

        /// <summary>Slot sizes and collapsed state remembered per layout preset, by preset name.</summary>
        public Dictionary<string, LayoutState> Presets { get; set; } = [];
    }

    /// <summary>Sizes (in DIPs) and collapsed state of the right and bottom slots of one layout preset.</summary>
    public sealed class LayoutState
    {
        public double RightWidth { get; set; }
        public double BottomHeight { get; set; }
        public bool RightCollapsed { get; set; }
        public bool BottomCollapsed { get; set; }
    }

    /// <summary>What was open when the app was closed, reopened at the next start.</summary>
    public sealed class SessionSettings
    {
        public string? LastDatabase { get; set; }
        public List<string> OpenTables { get; set; } = [];
        public List<string> PinnedTables { get; set; } = [];
        public string? ActiveTable { get; set; }
        /// <summary>The table open in the preview tab, if any (it is restored as a preview tab).</summary>
        public string? PreviewTable { get; set; }
        /// <summary>What the last table was made from: "Minimal", "Empty table" or a template's name.</summary>
        public string? LastNewTableSource { get; set; }
    }
}
