using System.ComponentModel;
using System.Data;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Altium_Library_Manager.Services.Settings;
using Altium_Library_Manager.Services;
using Altium_Library_Manager.Services.Templates;

namespace Altium_Library_Manager
{
    /// <summary>
    /// An open database table: one tab of the central area. Each tab owns its own view (toolbar and DataGrid), so
    /// switching tabs keeps its columns, selection, scroll position and search instead of rebuilding the grid.
    /// The data is loaded on first activation, so restoring many tabs at startup costs nothing.
    /// Row search: TableTab.Search.cs; editing rows: TableTab.Editing.cs.
    /// </summary>
    public sealed partial class TableTab : INotifyPropertyChanged
    {
        /// <summary>Look of all table grids (Options → Tables); applied to open tabs by <see cref="ApplyGridSettings"/>.</summary>
        public static double RowHeight { get; set; } = 20;

        /// <summary>Column width options and the remembered widths (the live Options → Tables settings).</summary>
        public static TablesSettings Columns { get; set; } = new();

        /// <summary>Raised after a setting of the tables changed (a column width or the search columns were
        /// remembered), so the settings get saved.</summary>
        public static event Action? SettingsChanged;

        public static DataGridGridLinesVisibility GridLines { get; set; } = DataGridGridLinesVisibility.All;

        public static bool AlternatingRows { get; set; }

        public static bool FreezeFirstColumn { get; set; } = true;

        /// <summary>The live Options → Editing settings.</summary>
        public static EditingSettings Editing { get; set; } = new();

        // Text of a larger size needs a taller row than the chosen density gives
        private static double GridRowHeight => Math.Max(RowHeight, Math.Ceiling(Columns.FontSize * 1.45));

        /// <summary>Row, cell and column header styles of all table grids (the window's "GridRow", "GridCell" and
        /// "GridColumnHeader"); set before the first tab is created.</summary>
        public static Style? GridRowStyle { get; set; }

        public static Style? GridCellStyle { get; set; }

        public static Style? GridColumnHeaderStyle { get; set; }

        /// <summary>The text box of a cell being edited (the window's "GridEditingText").</summary>
        public static Style? GridEditingTextStyle { get; set; }
        // Columns shown first (in this order); the first column can be frozen (FreezeFirstColumn)
        public static readonly string[] LeadingColumns =
            ["Part Number", "Library Ref", "Library Path", "Footprint Ref", "Footprint Path", "Description"];

        private static readonly Style TrimmedCellStyle = CreateTrimmedCellStyle();

        private bool _isPinned;
        private bool _isPreview;
        private string? _appliedSizing;

        public TableTab(string name)
        {
            Name = name;
            Grid = CreateGrid();
            ApplyGridSettings();
            // handledEventsToo: the column header marks its gripper's drag as handled
            Grid.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Grid_ColumnResized), handledEventsToo: true);
            InitializeSearch();
            InitializeEditing();
            InitializeCells();
            View = new TableView(this);
        }

        public string Name { get; }

        public DataGrid Grid { get; }

        /// <summary>What the tab shows: the toolbar (search, editing) above the grid.</summary>
        public TableView View { get; }

        public DataTable? Table { get; private set; }

        /// <summary>Set while the data is being loaded, so it is loaded once even if the tab is activated again.</summary>
        public Task? Loading { get; set; }

        public bool IsPinned
        {
            get => _isPinned;
            set
            {
                if (_isPinned == value)
                    return;
                _isPinned = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PinMenuText));
            }
        }

        public string PinMenuText => IsPinned ? "Unpin tab" : "Pin tab";

        /// <summary>
        /// A preview tab (title in italics), as in VS Code: opened by a single click in the table list and replaced
        /// by the next table opened that way, until it is kept (double-click, pin, drag).
        /// </summary>
        public bool IsPreview
        {
            get => _isPreview;
            set
            {
                if (_isPreview == value)
                    return;
                _isPreview = value;
                OnPropertyChanged();
                // A temporary tab kept after all (double-click, pin…) is now one that opened to stay
                if (!value && Editing.EditOnOpen && Table != null && CanEdit)
                    IsEditing = true;
            }
        }

        /// <summary>Applies changed table options (<see cref="RowHeight"/>, <see cref="Columns"/>, …) to this tab's grid.</summary>
        public void ApplyGridSettings()
        {
            Grid.FontSize = Columns.FontSize;
            Grid.RowHeight = GridRowHeight;
            UpdateSelectedRow();
            Grid.GridLinesVisibility = GridLines;
            Grid.FrozenColumnCount = FreezeFirstColumn && Grid.Columns.Count > 0 ? 1 : 0;
            // Shading every other row: rows alternate between index 0 and 1, and the row style shades index 1
            Grid.AlternationCount = AlternatingRows ? 2 : 0;
            // Only changed width options re-fit the columns, so other option changes keep widths the user has set
            if (_appliedSizing != SizingSignature() && Grid.Columns.Count > 0)
                FitColumns();
        }

        // Shown by UI Automation / screen readers as the tab's name
        public override string ToString() => Name;

        /// <summary>
        /// Shows the table's rows (loaded or reloaded). <paramref name="fields"/> (types, key) and
        /// <paramref name="database"/> are what editing needs; without the fields the table can only be viewed.
        /// </summary>
        public void SetTable(DataTable table, IReadOnlyList<TemplateField>? fields, AccessDatabase database)
        {
            if (Table != null)
                DetachTable(Table);
            Table = table;
            Fields = fields;
            _fieldsByName.Clear();
            foreach (var field in fields ?? [])
                _fieldsByName[field.Name] = field;
            Database = database;
            // Access has no empty Yes/No: a new row starts as what it will be saved as
            foreach (var column in table.Columns.Cast<DataColumn>().Where(c => c.DataType == typeof(bool) && c.DefaultValue is DBNull))
                column.DefaultValue = false;
            AddSortTwins(table);
            AttachTable(table);

            // Nothing of the rows read before stays selected: the grid keeps a cell of a row it no longer has at index -1,
            // and the next refresh (a sort) throws
            SelectItem(null);
            Grid.ItemsSource = null;
            Grid.Columns.Clear();
            Grid.ItemsSource = table.DefaultView;
            GenerateColumns();
            ApplySearchNow();
            OnPropertyChanged(nameof(CanEdit));
            if (!CanEdit)
                IsEditing = false;
            UpdateEditing();
            // A fresh read: nothing to save or undo
            ClearHistory();
        }

        /// <summary>The table's fields, without the hidden columns added for sorting.</summary>
        public static IEnumerable<DataColumn> FieldColumns(DataTable table) => table.Columns.Cast<DataColumn>().Where(c => c.Expression.Length == 0);

        private static string SortTwinName(DataColumn column) => $"⁠sort {column.Ordinal}";

        // The grid sorts through DataView.Sort, a text that can't hold a comma in a column name ("Rds On (Max) @ Id, Vgs"
        // would throw). Such a column gets a hidden computed twin with a plain name, and the grid sorts by that: live, like
        // any column, and computed values change neither the row's state nor anything saved. (A LINQ-ordered view was
        // no way out: its comparer gets no row for some records during an edit, which corrupts the table's index.)
        private static void AddSortTwins(DataTable table)
        {
            foreach (var column in FieldColumns(table).Where(c => c.ColumnName.Contains(',') && c.DataType != typeof(byte[]) && c.DataType != typeof(Guid)).ToList())
                if (!table.Columns.Contains(SortTwinName(column)))
                    try
                    {
                        table.Columns.Add(SortTwinName(column), column.DataType, $"[{column.ColumnName.Replace(@"\", @"\\").Replace("]", @"\]")}]");
                    }
                    catch (Exception ex) when (ex is DataException or ArgumentException)
                    {
                        // A name the expression can't hold: the table still opens, this column just can't be sorted
                        System.Diagnostics.Debug.WriteLine($"No sorting for {column.ColumnName}: {ex.Message}");
                    }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        #region Grid

        private static DataGrid CreateGrid()
        {
            // Rows and columns are virtualized (see Grid_AutoGeneratedColumns) and row containers recycled.
            // A fixed row height avoids re-measuring while scrolling.
            var grid = new TableGrid
            {
                IsReadOnly = true,
                // Columns are made by GenerateColumns, so they stay when the search swaps the rows shown
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Extended,
                SelectionUnit = DataGridSelectionUnit.Cell,
                EnableRowVirtualization = true,
                FontSize = Columns.FontSize,
                RowHeight = GridRowHeight,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                RowStyle = GridRowStyle,
                CellStyle = GridCellStyle,
                ColumnHeaderStyle = GridColumnHeaderStyle
            };
            VirtualizingPanel.SetIsVirtualizing(grid, true);
            VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
            return grid;
        }

        // Space around the measured text: cell border and margins; header margins plus its (hidden) sort arrow
        private const double CellPadding = 12, HeaderPadding = 26;
        private const int SampleRows = 300;

        // Everything that decides the column widths; when it changes, the columns are fitted again
        private string SizingSignature() => FormattableString.Invariant(
            $"{Columns.FontSize}|{Columns.ColumnSizing}|{Columns.MinColumnWidth}|{Columns.MaxColumnWidth}|{Columns.FixedColumnWidth}|{Columns.RememberColumnWidths}|{RememberedWidths()?.Count ?? -1}");

        private Dictionary<string, double>? RememberedWidths() =>
            Columns.RememberColumnWidths && Columns.ColumnWidths.TryGetValue(Name, out var widths) ? widths : null;

        /// <summary>
        /// Gives every column a fixed width: the width the user set before (if remembered), the same width for all
        /// (<see cref="ColumnSizing.Fixed"/>), or computed from the data: its header and the longest values of a
        /// sample of rows, measured in the grid's font, between the minimum and maximum width. Computed rather than
        /// taken from layout because columns are virtualized, so off-screen columns are never measured (they came out
        /// a few pixels wide). Fixed widths also never change while scrolling, which would re-lay out the whole grid,
        /// and have no maximum, so a column can be dragged as wide as needed. Takes a few milliseconds per table.
        /// </summary>
        private void FitColumns()
        {
            _appliedSizing = SizingSignature();
            if (Table == null)
                return;

            var remembered = RememberedWidths();
            var fitContent = Columns.ColumnSizing == ColumnSizing.FitContent;
            var (min, max) = (Columns.MinColumnWidth, Math.Max(Columns.MaxColumnWidth, Columns.MinColumnWidth));

            var rows = Sample(Table.Rows);
            var pixelsPerDip = VisualTreeHelper.GetDpi(Grid).PixelsPerDip;
            var cellFont = new Typeface(Grid.FontFamily, Grid.FontStyle, Grid.FontWeight, Grid.FontStretch);
            var headerFont = new Typeface(Grid.FontFamily, Grid.FontStyle, FontWeights.SemiBold, Grid.FontStretch);
            double TextWidth(string text, Typeface font) => text.Length == 0 ? 0 :
                new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    font, Grid.FontSize, Brushes.Black, pixelsPerDip).WidthIncludingTrailingWhitespace;

            foreach (var column in Grid.Columns)
            {
                var name = column.Header as string ?? "";
                double width;
                if (remembered != null && remembered.TryGetValue(name, out var set))
                    width = set;
                else if (!fitContent)
                    width = Columns.FixedColumnWidth;
                else
                {
                    width = TextWidth(name, headerFont) + HeaderPadding;
                    if (Table.Columns[name] is DataColumn dataColumn)
                    {
                        // The longest strings are nearly always the widest; only a few of them are measured
                        foreach (var text in rows.Select(r => r.IsNull(dataColumn) ? "" : Convert.ToString(r[dataColumn]) ?? "")
                                     .OrderByDescending(s => s.Length).Take(3))
                            width = Math.Max(width, TextWidth(text, cellFont) + CellPadding);
                    }
                    width = Math.Clamp(Math.Ceiling(width), min, max);
                }
                column.MaxWidth = double.PositiveInfinity;
                column.Width = new DataGridLength(width);
            }
        }

        /// <summary>Remembers the width of a column whose edge the user dragged (Options → Tables).</summary>
        private void Grid_ColumnResized(object sender, DragCompletedEventArgs e)
        {
            if (!Columns.RememberColumnWidths ||
                e.OriginalSource is not Thumb { TemplatedParent: DataGridColumnHeader { Column: { } own } } thumb)
                return;
            // A header's left gripper resizes the column before it
            var column = thumb.Name == "PART_LeftHeaderGripper"
                ? Grid.Columns.Where(c => c.DisplayIndex < own.DisplayIndex).MaxBy(c => c.DisplayIndex)
                : own;
            if (column?.Header is not string name)
                return;

            if (!Columns.ColumnWidths.TryGetValue(Name, out var widths))
                Columns.ColumnWidths[Name] = widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            widths[name] = Math.Round(column.ActualWidth);
            _appliedSizing = SizingSignature();   // already applied, no need to fit again
            SettingsChanged?.Invoke();
        }

        /// <summary>The first rows plus rows spread over the rest of a large table (not rows deleted but not saved yet,
        /// whose values can't be read).</summary>
        private static List<DataRow> Sample(DataRowCollection rows)
        {
            var sample = new List<DataRow>();
            for (var i = 0; i < Math.Min(rows.Count, SampleRows); i++)
                sample.Add(rows[i]);
            if (rows.Count > SampleRows)
            {
                var step = Math.Max((rows.Count - SampleRows) / SampleRows, 1);
                for (var i = SampleRows; i < rows.Count; i += step)
                    sample.Add(rows[i]);
            }
            sample.RemoveAll(r => r.RowState == DataRowState.Deleted);
            return sample;
        }

        private static Style CreateTrimmedCellStyle()
        {
            // The tooltip is filled only on hover instead of a binding in every cell,
            // so scrolling doesn't create thousands of extra bindings
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, string.Empty));
            style.Setters.Add(new EventSetter(FrameworkElement.ToolTipOpeningEvent, new ToolTipEventHandler(Cell_ToolTipOpening)));
            return style;
        }

        private static void Cell_ToolTipOpening(object sender, ToolTipEventArgs e)
        {
            var textBlock = (TextBlock)sender;
            if (string.IsNullOrEmpty(textBlock.Text))
                e.Handled = true;
            else
                textBlock.ToolTip = textBlock.Text;
        }

        /// <summary>
        /// One column per field, made here rather than generated by the grid: generated columns would be rebuilt
        /// whenever the search changes the rows shown (losing widths and sorting). The Altium fields come first.
        /// </summary>
        private void GenerateColumns()
        {
            foreach (var column in DataGrid.GenerateColumns((IItemProperties)Grid.Items))
            {
                if (column.Header is string field && Table!.Columns[field] is { } source)
                {
                    if (source.Expression.Length > 0)
                        continue;
                    if (field.Contains(','))
                    {
                        if (Table.Columns.Contains(SortTwinName(source)))
                            column.SortMemberPath = SortTwinName(source);
                        else
                            column.CanUserSort = false;
                    }
                }

                // In a binding path / and . mean something else: "Size / Dimension" as a path leaves the cells empty and
                // fails the row's edit. Such a name goes in brackets, with ^ before the characters special there.
                if (column is DataGridBoundColumn { Binding: System.Windows.Data.Binding binding } && column.Header is string header &&
                    header.AsSpan().IndexOfAny("/.[]") >= 0)
                    binding.Path = new PropertyPath("[" + System.Text.RegularExpressions.Regex.Replace(header, @"[\^,\[\]()']", "^$0") + "]");

                if (column is DataGridTextColumn textColumn)
                {
                    // Long values (URLs, paths) don't stretch the table (widths: FitColumns): they are trimmed, and the
                    // full text is shown in a tooltip
                    textColumn.ElementStyle = TrimmedCellStyle;
                    textColumn.EditingElementStyle = GridEditingTextStyle;
                }
                column.IsReadOnly = column.Header is string name && IsReadOnlyColumn(name);
                Grid.Columns.Add(column);
            }

            var leading = LeadingColumns
                .Select(name => AccessDatabase.FindColumn(Table!, name)?.ColumnName)
                .OfType<string>()
                .Select(name => Grid.Columns.FirstOrDefault(c => Equals(c.Header, name)))
                .OfType<DataGridColumn>()
                .ToList();
            var ordered = leading.Concat(Grid.Columns.Except(leading)).ToList();
            for (var i = 0; i < ordered.Count; i++)
                ordered[i].DisplayIndex = i;

            Grid.FrozenColumnCount = FreezeFirstColumn && Grid.Columns.Count > 0 ? 1 : 0;

            // Columns are virtualized too: measured on a 62-column table, creating every column's cells for each row
            // made a vertical scroll step about 4x slower (a page took ~36 ms, over the 16 ms frame budget). Sideways
            // steps cost ~8 ms with it; what made them stutter was auto-sized columns, fixed in FitColumns.
            Grid.EnableColumnVirtualization = true;
            FitColumns();
        }

        #endregion
    }

    /// <summary>One field of the selected row, for the details panel. Its value follows the row's (see
    /// MainWindow.RefreshDetails), so it updates in place while the row is edited.</summary>
    public sealed class RowField(string name, string value, FieldLink link = FieldLink.None) : INotifyPropertyChanged
    {
        private string _value = value;
        private bool _canBrowse;

        public string Name { get; } = name;

        /// <summary>What the value points to: such a field has a button to pick a file, and Ctrl+click opens it.</summary>
        public FieldLink Link { get; } = link;

        public bool HasBrowse => Link != FieldLink.None;

        public string BrowseTip => FieldLinks.BrowseTip(Link);

        /// <summary>A drop-down arrow for a Ref field (a list), a folder for a path (a file dialog).</summary>
        public string BrowseGlyph => FieldLinks.IsRef(Link) ? "" : "";

        /// <summary>The field can be changed now (the table's Edit is on).</summary>
        public bool CanBrowse
        {
            get => _canBrowse;
            set
            {
                if (_canBrowse == value)
                    return;
                _canBrowse = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanBrowse)));
            }
        }

        public string Value
        {
            get => _value;
            set
            {
                if (_value == value)
                    return;
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
