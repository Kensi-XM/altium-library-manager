using System.Data;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Altium_Library_Manager.Services;

namespace Altium_Library_Manager
{
    /// <summary>What the table's context menu asks of a path or link field; the window does it, as for the details.</summary>
    public enum FieldAction
    {
        Open,
        Browse
    }

    /// <summary>True when the row's item is the one in the grid's Tag: shades the current row (the window's "GridRow").</summary>
    public sealed class SameRowConverter : IMultiValueConverter
    {
        public static readonly SameRowConverter Instance = new();

        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
            values is [DataRowView a, DataRowView b] && a.Row == b.Row;

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// The table grid. A DataGrid announces every selected cell to UI Automation clients (screen readers and other
    /// tools, when one runs): for Ctrl+A on a large table, over a million cells, which froze the app for 11 seconds
    /// (measured; it took 50 ms with no client listening). A selection that large is reported to the tab only.
    /// </summary>
    public sealed class TableGrid : DataGrid
    {
        private const int MaxAnnouncedCells = 5000;

        /// <summary>Instead of SelectedCellsChanged, for a selection change too large to announce.</summary>
        public event Action? LargeSelectionChanged;

        public TableGrid() =>
            // A style is looked up by the exact type: this one uses the DataGrid's, of the theme
            SetResourceReference(StyleProperty, typeof(DataGrid));

        protected override void OnSelectedCellsChanged(SelectedCellsChangedEventArgs e)
        {
            if (e.AddedCells.Count + e.RemovedCells.Count <= MaxAnnouncedCells)
                base.OnSelectedCellsChanged(e);
            else
                LargeSelectionChanged?.Invoke();
        }
    }

    /// <summary>
    /// The grid selects cells: by dragging, with Shift and Ctrl, Ctrl+A. The row of the current cell is the tab's
    /// selected row, shown in the details and preview, and is shaded. The selected cells can be cleared, filled and
    /// pasted into together, each as one step of the history (TableTab.History.cs).
    /// </summary>
    public sealed partial class TableTab
    {
        // Far beyond any table here; what lands on the clipboard by mistake (a whole log) shouldn't stall the app
        private const int MaxPasteLength = 50_000_000;

        private DataRowView? _selectedRow;
        private bool _showingField;

        /// <summary>The row of the current cell changed.</summary>
        public event Action? SelectedRowChanged;

        /// <summary>The user moved to a cell of another column (clicked, arrow keys): its field, for the details to
        /// select too.</summary>
        public event Action<string>? CurrentFieldChanged;

        /// <summary>The table's context menu asks to open a field of the selected row, or pick its value; the element
        /// is where a list of values opens.</summary>
        public event Action<string, FieldAction, FrameworkElement>? FieldActionRequested;

        public void RequestFieldAction(string field, FieldAction action, FrameworkElement placement) =>
            FieldActionRequested?.Invoke(field, action, placement);

        /// <summary>The row of the current cell (not one that was deleted meanwhile).</summary>
        public DataRowView? SelectedRow => IsLive(_selectedRow) ? _selectedRow : null;

        // A row being added isn't in the table yet, but has values
        private static bool IsLive(DataRowView? view) =>
            view != null && (view.IsNew || view.Row.RowState is not (DataRowState.Deleted or DataRowState.Detached));

        private void InitializeCells()
        {
            Grid.SelectedCellsChanged += (_, _) => UpdateSelectedRow();
            ((TableGrid)Grid).LargeSelectionChanged += UpdateSelectedRow;
            Grid.CurrentCellChanged += (_, _) =>
            {
                UpdateSelectedRow();
                if (!_showingField && Grid.CurrentColumn is { Header: string name })
                    CurrentFieldChanged?.Invoke(name);
            };
        }

        private void UpdateSelectedRow()
        {
            var view = Grid.CurrentCell.Item as DataRowView;
            if (view == null && Grid.SelectedCells.Count > 0)
                view = Grid.SelectedCells[0].Item as DataRowView;
            if (!IsLive(view))
                view = null;
            var changed = view?.Row != _selectedRow?.Row;
            _selectedRow = view;
            // The row style shades the row that is the tag
            Grid.Tag = Columns.HighlightCurrentRow ? view : null;
            if (changed)
                SelectedRowChanged?.Invoke();
        }

        private DataGridColumn? FirstColumn() =>
            Grid.Columns.Where(c => c.Visibility == Visibility.Visible).MinBy(c => c.DisplayIndex);

        /// <summary>Makes one cell of a row (in the current column, unless given) the selected and current one.</summary>
        public void SelectItem(DataRowView? item, DataGridColumn? column = null)
        {
            Grid.UnselectAllCells();
            column ??= Grid.CurrentColumn ?? FirstColumn();
            if (item == null || column == null)
            {
                Grid.CurrentCell = default;
                UpdateSelectedRow();
                return;
            }
            var cell = new DataGridCellInfo(item, column);
            Grid.CurrentCell = cell;
            AddToSelection(cell);
            UpdateSelectedRow();
        }

        // The grid refuses a cell already selected with an exception
        private void AddToSelection(DataGridCellInfo cell)
        {
            if (!Grid.SelectedCells.Contains(cell))
                Grid.SelectedCells.Add(cell);
        }

        /// <summary>Selects these rows' cells in the current column, the first one current.</summary>
        private void SelectItems(IReadOnlyList<DataRowView> items)
        {
            SelectItem(items.Count > 0 ? items[0] : null);
            if (Grid.CurrentColumn is { } column)
                foreach (var item in items.Skip(1))
                    AddToSelection(new DataGridCellInfo(item, column));
        }

        /// <summary>The rows with a selected cell, in no particular order (not the empty row for a new one).</summary>
        public List<DataRow> SelectedRows() => Grid.SelectedCells.Select(c => c.Item).OfType<DataRowView>()
            .Where(v => !v.IsNew && IsLive(v)).Select(v => v.Row).Distinct().ToList();

        public void SelectColumn(DataGridColumn column)
        {
            var current = SelectedRow;
            Grid.UnselectAllCells();
            foreach (var item in Grid.Items.OfType<DataRowView>())
                AddToSelection(new DataGridCellInfo(item, column));
            if (current != null)
                Grid.CurrentCell = new DataGridCellInfo(current, column);
        }

        /// <summary>Selects every cell of the rows with a selected cell.</summary>
        public void SelectWholeRows()
        {
            var items = Grid.SelectedCells.Select(c => c.Item).OfType<DataRowView>().Distinct().ToList();
            // Quicker than cell by cell, which a whole table would take long for
            if (items.Count >= Grid.Items.OfType<DataRowView>().Count())
            {
                Grid.SelectAllCells();
                return;
            }
            foreach (var item in items)
                foreach (var column in Grid.Columns.Where(c => c.Visibility == Visibility.Visible))
                    AddToSelection(new DataGridCellInfo(item, column));
        }

        /// <summary>
        /// Selects a field (one picked in the details): its cell in the selected row becomes the selected and current
        /// one, scrolled into view. Without moving the keyboard there, and without disturbing a cell being typed in.
        /// </summary>
        public void ShowField(string name)
        {
            var column = Grid.Columns.FirstOrDefault(c => string.Equals(c.Header as string, name, StringComparison.OrdinalIgnoreCase));
            var typing = Keyboard.FocusedElement is TextBox box && IsInGrid(box);
            if (column == null || typing || SelectedRow is not { } item || Grid.CurrentColumn == column)
                return;
            _showingField = true;
            try
            {
                SelectItem(item, column);
                Grid.ScrollIntoView(item, column);
            }
            finally
            {
                _showingField = false;
            }
        }

        private bool IsInGrid(DependencyObject element)
        {
            for (var current = element; current != null; current = VisualTreeHelper.GetParent(current))
                if (current == Grid)
                    return true;
            return false;
        }

        #region Adding many rows

        /// <summary>The Add elements button: the window shows the dialog (it has the database and libraries).</summary>
        public event Action? AddElementsRequested;

        public void RequestAddElements()
        {
            if (CheckEditing() && TryFinishEditing(Window.GetWindow(View)))
                AddElementsRequested?.Invoke();
        }

        /// <summary>The fields a new row can be given.</summary>
        public IReadOnlyList<DataColumn> NewRowColumns() => Table == null ? [] : EditableColumns().ToList();

        /// <summary>The Part Numbers in the table (not those of rows deleted but not saved).</summary>
        public HashSet<string> PartNumbers() => CountPartNumbers()?.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        public DataColumn? PartNumberField => PartNumberColumn;

        /// <summary>Why this text can't go into a field of a new row, or null.</summary>
        public string? NewValueProblem(DataColumn column, string text)
        {
            if (ParseField(column, text, out _) is { } problem)
                return problem;
            return text.Length > 0 && FieldOf(column.ColumnName) is { Type: Services.Templates.FieldType.Text } field && text.Length > field.Size
                ? $"\"{column.ColumnName}\" takes at most {field.Size} characters; \"{text}\" has {text.Length}."
                : null;
        }

        /// <summary>
        /// Adds rows with these field texts (checked with <see cref="NewValueProblem"/>), as one step of the history. The
        /// search is left, so they can be seen; the first one is selected.
        /// </summary>
        public void AddRows(IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
        {
            if (Table == null || rows.Count == 0 || !TryFinishEditing(Window.GetWindow(View)))
                return;
            if (_filtered)
            {
                Query = "";
                ApplySearchNow();
            }

            var steps = new List<RowStep>();
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                foreach (var fields in rows)
                {
                    var values = Table.Columns.Cast<DataColumn>().Select(c => c.DefaultValue).ToArray();
                    foreach (var (name, text) in fields)
                    {
                        // A field left empty keeps its default
                        if (Table.Columns[name] is not { } column || IsReadOnlyColumn(column.ColumnName) || text.Length == 0 && column.DefaultValue is not DBNull ||
                            ParseField(column, text, out var value) != null)
                            continue;
                        values[column.Ordinal] = value is string { Length: 0 } && FieldOf(column.ColumnName) is not { Required: true } ? DBNull.Value : value;
                    }
                    var row = Table.NewRow();
                    var step = new RowStep(row, RowSnapshot.Absent, new RowSnapshot(true, values));
                    Apply(row, step.After);
                    steps.Add(step);
                }
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
            Record(steps.Count == 1 ? "Add 1 row" : $"Add {steps.Count} rows", steps);
            SelectRow(steps[0].Row);
        }

        #endregion

        #region Changing the selected cells

        // The selected cells of rows in the table, as table columns
        private List<(DataRow Row, DataColumn Column)> SelectedTableCells()
        {
            var cells = new List<(DataRow, DataColumn)>();
            if (Table == null)
                return cells;
            foreach (var cell in Grid.SelectedCells)
                if (cell.Item is DataRowView { IsNew: false } view && IsLive(view) && cell.Column?.Header is string name && Table.Columns[name] is { } column)
                    cells.Add((view.Row, column));
            return cells;
        }

        // Rows in the order shown
        private Dictionary<DataRow, int> ShownOrder()
        {
            var order = new Dictionary<DataRow, int>();
            foreach (var view in Grid.Items.OfType<DataRowView>())
                if (!view.IsNew)
                    order[view.Row] = order.Count;
            return order;
        }

        private void ShowCellsProblem(string message, string detail) =>
            AppDialog.Error(Window.GetWindow(View), "Change cells", message, detail);

        /// <summary>Empties the selected cells (Del).</summary>
        public void ClearSelectedCells() => SetSelectedCells("Clear", "");

        /// <summary>Puts one typed value into all the selected cells.</summary>
        public void FillSelectedCells()
        {
            var cells = SelectedTableCells();
            if (cells.Count == 0 || !CheckEditing())
                return;
            var first = SelectedRow?.Row is { } row && Grid.CurrentColumn?.Header is string name && Table!.Columns[name] is { } column && !row.IsNull(column)
                ? Convert.ToString(row[column]) ?? ""
                : "";
            if (AppDialog.Prompt(Window.GetWindow(View), "Fill cells",
                    cells.Count == 1 ? "New value of the cell" : $"New value of the {cells.Count} selected cells", first, "Fill") is { } value)
                SetSelectedCells("Fill", value);
        }

        private void SetSelectedCells(string verb, string text)
        {
            if (!CheckEditing())
                return;
            var changes = new List<(DataRow, DataColumn, object)>();
            foreach (var (row, column) in SelectedTableCells())
            {
                if (IsReadOnlyColumn(column.ColumnName))
                    continue;
                if (ParseField(column, text, out var value) is { } problem)
                {
                    ShowCellsProblem("The cells weren't changed", problem);
                    return;
                }
                changes.Add((row, column, value));
            }
            ChangeCells(verb, changes);
        }

        /// <summary>In each selected column, the first selected cell's value goes into the ones below it.</summary>
        public void FillDown()
        {
            if (!CheckEditing())
                return;
            var order = ShownOrder();
            var changes = new List<(DataRow, DataColumn, object)>();
            foreach (var group in SelectedTableCells().Where(c => !IsReadOnlyColumn(c.Column.ColumnName)).GroupBy(c => c.Column))
            {
                var cells = group.Where(c => order.ContainsKey(c.Row)).OrderBy(c => order[c.Row]).ToList();
                foreach (var (row, column) in cells.Skip(1))
                    changes.Add((row, column, cells[0].Row[column]));
            }
            ChangeCells("Fill down", changes);
        }

        /// <summary>
        /// Ctrl+C: text with tabs between the cells, which spreadsheets read, and Paste here too. Done here because the
        /// grid's own copy builds several formats cell by cell, which takes very long for a whole table. A tab or a
        /// line break inside a value becomes a space, so it stays one cell.
        /// </summary>
        public bool CopySelectedCells()
        {
            var text = SelectedCellsText();
            if (text.Length == 0)
                return false;
            try
            {
                Clipboard.SetText(text);
                Status?.Invoke(Grid.SelectedCells.Count == 1 ? "Copied 1 cell" : $"Copied {Grid.SelectedCells.Count} cells");
                return true;
            }
            catch (ExternalException)
            {
                // Another program has the clipboard open
                Status?.Invoke("The clipboard is busy: try again");
                return false;
            }
        }

        // In the order shown; a cell not selected in a selected row and column stays empty, so the block keeps its shape
        private string SelectedCellsText()
        {
            var cells = SelectedTableCells();
            if (cells.Count == 0)
                return "";
            var order = ShownOrder();
            var used = cells.Select(c => c.Column.ColumnName).ToHashSet();
            var columns = Grid.Columns.OrderBy(c => c.DisplayIndex).Select(c => c.Header).OfType<string>().Where(used.Contains).ToList();
            var text = new System.Text.StringBuilder();
            foreach (var row in cells.GroupBy(c => c.Row).OrderBy(g => order.GetValueOrDefault(g.Key)))
            {
                var selected = row.Select(c => c.Column.ColumnName).ToHashSet();
                text.AppendJoin('\t', columns.Select(name => selected.Contains(name) ? CellText(row.Key, name) : ""));
                text.Append("\r\n");
            }
            return text.ToString();
        }

        private static string CellText(DataRow row, string column) =>
            (Convert.ToString(row[column], CultureInfo.CurrentCulture) ?? "").Replace('\t', ' ').Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

        public void CutSelectedCells()
        {
            if (!CheckEditing())
                return;
            // Not cleared if it didn't reach the clipboard (another program may have it open)
            if (CopySelectedCells())
                ClearSelectedCells();
        }

        /// <summary>
        /// Ctrl+V. One value goes into all the selected cells; a block (from a spreadsheet, or copied here) goes
        /// from the top left selected cell down and to the right, as far as the table reaches.
        /// </summary>
        public void Paste()
        {
            if (!CheckEditing())
                return;
            string text;
            try
            {
                text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            }
            catch (ExternalException)
            {
                // Another program has the clipboard open
                Status?.Invoke("The clipboard is busy: try again");
                return;
            }
            PasteText(text);
        }

        private void PasteText(string text)
        {
            var selected = SelectedTableCells();
            if (selected.Count == 0 || text.Length == 0)
                return;
            if (text.Length > MaxPasteLength)
            {
                Status?.Invoke("The clipboard holds too much text to paste");
                return;
            }

            var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(l => l.Split('\t')).ToList();
            var targets = new List<(DataRow, DataColumn, string)>();
            var outside = 0;
            if (lines is [[var single]])
                targets.AddRange(selected.Select(c => (c.Row, c.Column, single)));
            else
            {
                var order = ShownOrder();
                var rows = order.OrderBy(p => p.Value).Select(p => p.Key).ToList();
                var columns = Grid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex)
                    .Select(c => c.Header as string).ToList();
                var top = selected.Min(c => order.GetValueOrDefault(c.Row, int.MaxValue));
                var left = selected.Min(c => columns.IndexOf(c.Column.ColumnName) is var i and >= 0 ? i : int.MaxValue);
                for (var r = 0; r < lines.Count; r++)
                {
                    for (var c = 0; c < lines[r].Length; c++)
                    {
                        if ((long)top + r >= rows.Count || (long)left + c >= columns.Count)
                            outside++;
                        else if (Table!.Columns[columns[left + c] ?? ""] is { } column)
                            targets.Add((rows[top + r], column, lines[r][c]));
                    }
                }
            }

            var changes = new List<(DataRow, DataColumn, object)>();
            foreach (var (row, column, value) in targets.Where(t => !IsReadOnlyColumn(t.Item2.ColumnName)))
            {
                if (ParseField(column, value, out var parsed) is { } problem)
                {
                    ShowCellsProblem("Nothing was pasted", problem);
                    return;
                }
                changes.Add((row, column, parsed));
            }
            var pasted = ChangeCells("Paste", changes);
            if (pasted && outside > 0)
                Status?.Invoke($"Pasted; {outside} values past the edge of the table were left out");
        }

        private bool CheckEditing()
        {
            if (_isEditing)
                return true;
            Status?.Invoke("Turn on Edit (Ctrl+E) to change cells");
            return false;
        }

        /// <summary>
        /// Changes many cells as one step of the history, all or none: every changed row is checked first, as when a row
        /// is edited, and a Part Number may not end up on two rows of the change. AutoNumber and binary cells are left.
        /// True when cells were changed.
        /// </summary>
        private bool ChangeCells(string verb, IReadOnlyList<(DataRow Row, DataColumn Column, object Value)> changes)
        {
            if (Table == null || !TryFinishEditing(Window.GetWindow(View)))
                return false;
            var befores = new Dictionary<DataRow, RowSnapshot>();
            var afters = new Dictionary<DataRow, object[]>();
            foreach (var (row, column, value) in changes)
            {
                if (row.RowState is DataRowState.Deleted or DataRowState.Detached)
                    continue;
                if (!afters.TryGetValue(row, out var after))
                {
                    befores[row] = Snapshot(row, DataRowVersion.Current);
                    afters[row] = after = (object[])befores[row].Values.Clone();
                }
                after[column.Ordinal] = value is string { Length: 0 } && FieldOf(column.ColumnName) is not { Required: true } ? DBNull.Value : value;
            }

            var changed = afters.Where(p => !new RowSnapshot(true, p.Value).SameAs(befores[p.Key])).ToList();
            if (changed.Count == 0)
            {
                Status?.Invoke("Nothing to change");
                return false;
            }

            var pn = PartNumberColumn;
            var partNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _partNumbers = CountPartNumbers();
            try
            {
                foreach (var (row, after) in changed)
                {
                    var partNumberChanged = pn != null && !Equals(after[pn.Ordinal], befores[row].Values[pn.Ordinal]);
                    var problem = RowProblem(row, c => after[c.Ordinal], partNumberChanged);
                    if (problem == null && partNumberChanged && after[pn!.Ordinal] is string text && !partNumbers.Add(text.Trim()))
                        problem = $"Two rows would get the {pn.ColumnName} \"{text.Trim()}\". Each component needs its own.";
                    if (problem != null)
                    {
                        _partNumbers = null;
                        SelectRow(row);
                        ShowCellsProblem("The cells weren't changed", $"{RowTitle(row)}: {problem}");
                        return false;
                    }
                }
            }
            finally
            {
                _partNumbers = null;
            }

            var steps = changed.Select(p => new RowStep(p.Key, befores[p.Key], new RowSnapshot(true, p.Value))).ToList();
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                foreach (var step in steps)
                    Apply(step.Row, step.After);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
            var cells = steps.Sum(s => s.Before.Values.Zip(s.After.Values).Count(p => !Equals(p.First, p.Second)));
            Record(cells == 1 ? $"{verb} 1 cell" : $"{verb} {cells} cells", steps);
            return true;
        }

        #endregion
    }
}
