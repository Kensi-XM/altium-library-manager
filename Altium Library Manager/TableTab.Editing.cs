using System.ComponentModel;
using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Altium_Library_Manager.Services;
using Altium_Library_Manager.Services.Templates;

namespace Altium_Library_Manager
{
    /// <summary>
    /// Editing rows in the grid, once the tab's Edit switch is on (tables open read-only). A row's changes are kept
    /// when it is left or Enter is pressed (Esc undoes them until then), as one step of the tab's history; nothing
    /// reaches the database until the table is saved (TableTab.History.cs). A row whose values can't be kept (too long
    /// for the field, a Part Number another row has) stays in editing with the reason shown.
    /// </summary>
    public sealed partial class TableTab
    {
        private bool _isEditing;
        // Why the last row's changes couldn't be kept; shown once the grid is done ending the edit (or by TryFinishEditing)
        private string? _editError;
        private bool _quietEdit;
        // Ended row edits whose history step is still to be recorded (just after the grid's commit; see FlushEdits)
        private int _pendingEdits;

        /// <summary>The table's fields as in the database (types, sizes, key); null if they couldn't be read.</summary>
        public IReadOnlyList<TemplateField>? Fields { get; private set; }

        public AccessDatabase? Database { get; private set; }

        /// <summary>A message for the status line (changed, saved, undone, …).</summary>
        public event Action<string>? Status;

        /// <summary>Rows changed (edited, undone, saved), so the details and preview of the selected row may be out of date.</summary>
        public event Action? RowsChanged;

        /// <summary>The Reload button or F5: the window reads the table again.</summary>
        public event Action? ReloadRequested;

        public bool CanEdit => Table != null && Fields != null && Database != null;

        /// <summary>The Edit switch: cells can be changed and rows added and deleted. Turning it off keeps the row
        /// being edited first; changes not saved yet stay (and can still be saved or undone).</summary>
        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                if (_isEditing == value || value && !CanEdit)
                {
                    OnPropertyChanged();   // a toggle button bound to it goes back to the actual state
                    return;
                }
                if (!value && !TryFinishEditing(Window.GetWindow(View)))
                {
                    OnPropertyChanged();
                    return;
                }
                _isEditing = value;
                // An edited table stays open: a preview tab would be replaced by the next table clicked
                if (value)
                    IsPreview = false;
                UpdateEditing();
                OnPropertyChanged();
                Status?.Invoke(value
                    ? $"Editing {Name}: changes are kept in the table until you save it (Ctrl+S); Ctrl+Z undoes them"
                    : $"{Name} is read-only again");
            }
        }

        /// <summary>Before anything that needs the table's edits ended (saving, undoing, closing): whoever lets values
        /// of this table be typed outside the grid (the details) applies the one being typed.</summary>
        public event Action? FinishingEdits;

        /// <summary>A row's values changed (a cell left, a row edit undone or cancelled, a change from the details):
        /// whoever shows that row shows it again.</summary>
        public event Action<DataRow>? RowValuesChanged;

        private void InitializeEditing()
        {
            Grid.RowEditEnding += Grid_RowEditEnding;
            Grid.CellEditEnding += Grid_CellEditEnding;
        }

        /// <summary>
        /// A cell's new value goes into the row as soon as the cell is left, not only when the row is (the grid would
        /// keep it to itself until then), so the details and preview show it at once. The row is still being edited:
        /// Esc still undoes all its changes, and its history step is still recorded when it is left.
        /// </summary>
        private void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit)
                return;
            // The row's bindings keep a left cell's value under its field name, read back later as a path, which a name
            // like "Size / Dimension" can't be: this value goes into the row now, while the cell still has its binding
            if (e.Column is DataGridBoundColumn { Binding: System.Windows.Data.Binding { Path.Path: ['[', ..] } })
                System.Windows.Data.BindingOperations.GetBindingExpression(e.EditingElement,
                    e.EditingElement is CheckBox ? System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty : TextBox.TextProperty)?.UpdateSource();
            var container = e.Row;
            var item = container.Item;
            Grid.Dispatcher.BeginInvoke(() =>
            {
                // Still the same row being edited (Enter ends the row too; the container may show another row by now)
                if (Grid.Items is IEditableCollectionView view && (view.CurrentEditItem ?? view.CurrentAddItem) == item &&
                    container.Item == item && container.BindingGroup is { } bindings)
                    bindings.UpdateSources();
            }, DispatcherPriority.Background);
        }

        private void Table_ColumnChanged(object sender, DataColumnChangeEventArgs e) => RowValuesChanged?.Invoke(e.Row);

        private void UpdateEditing()
        {
            Grid.IsReadOnly = !_isEditing;
            // The empty row at the end, for a new row. Not while searching: a new row wouldn't match and would vanish.
            Grid.CanUserAddRows = _isEditing && !_filtered;
        }

        // By name: asked for every column of every row a change touches
        private readonly Dictionary<string, TemplateField> _fieldsByName = new(StringComparer.OrdinalIgnoreCase);

        private TemplateField? FieldOf(string column) => _fieldsByName.GetValueOrDefault(column);

        /// <summary>AutoNumber values are given by Access; binary data can't be edited as text.</summary>
        private bool IsReadOnlyColumn(string column) =>
            FieldOf(column)?.Type == FieldType.AutoNumber || Table?.Columns[column] is { } c && (c.DataType == typeof(byte[]) || c.Expression.Length > 0);

        private IEnumerable<DataColumn> EditableColumns() =>
            Table!.Columns.Cast<DataColumn>().Where(c => !IsReadOnlyColumn(c.ColumnName));

        private bool IsRowEditing => Grid.Items is IEditableCollectionView view && (view.IsAddingNew || view.IsEditingItem);

        private static string RowTitle(DataRow row)
        {
            var version = row.RowState == DataRowState.Deleted ? DataRowVersion.Original : DataRowVersion.Default;
            var column = AccessDatabase.FindColumn(row.Table, "Part Number") ?? row.Table.Columns[0];
            var value = row.IsNull(column, version) ? null : Convert.ToString(row[column, version]);
            return string.IsNullOrWhiteSpace(value) ? "(no part number)" : $"\"{value}\"";
        }

        #region Grid edits

        private void Grid_RowEditEnding(object? sender, DataGridRowEditEndingEventArgs e)
        {
            if (e.EditAction == DataGridEditAction.Commit && e.Row.Item is DataRowView view)
            {
                // The grid moves the typed values into the row only after this event; moved here, so they can be
                // checked first (and kept in the row, still being edited, if they can't be kept)
                if (e.Row.BindingGroup is { } bindings && !bindings.UpdateSources())
                {
                    RefuseEdit(e, "A value doesn't fit its field (the cell marked in red): a number field takes only a number.");
                    return;
                }
                var row = view.Row;
                var table = Table;
                if (view.IsNew && IsEmpty(view))
                {
                    // An added row left without typing anything: dropped once the grid has committed it
                    AfterCommit(() =>
                    {
                        if (row.RowState == DataRowState.Added && row.Table == table)
                            table.Rows.Remove(row);
                        UpdateCount();
                    });
                }
                else
                {
                    // A cleared cell is an empty field rather than a zero-length text (which Access may refuse),
                    // unless the field is required
                    foreach (var column in EditableColumns())
                        if (view[column.Ordinal] is string { Length: 0 } && FieldOf(column.ColumnName) is not { Required: true })
                            view[column.Ordinal] = DBNull.Value;

                    var partNumberChanged = PartNumberColumn is { } pn &&
                                            (view.IsNew || !Equals(view[pn.Ordinal], row[pn, DataRowVersion.Current]));
                    if (RowProblem(row, c => view[c.Ordinal], partNumberChanged) is { } problem)
                    {
                        RefuseEdit(e, problem);
                        return;
                    }

                    // One step of the history: the row as it was and as it is now
                    var before = view.IsNew ? RowSnapshot.Absent : Snapshot(row, DataRowVersion.Current);
                    var after = new RowSnapshot(true, Table!.Columns.Cast<DataColumn>().Select(c => view[c.Ordinal]).ToArray());
                    var description = view.IsNew ? $"Add row {RowTitle(row)}" : $"Edit row {RowTitle(row)}";
                    AfterCommit(() =>
                    {
                        // Not for a table read again meanwhile (its history starts afresh)
                        if (Table != table || row.RowState == DataRowState.Detached)
                            return;
                        Normalize(row);
                        if (!before.SameAs(after))
                            Record(description, [new RowStep(row, before, after)]);
                    });
                }
            }
            // Esc: the row's values are back to what they were (which the row reports no change for)
            if (e.EditAction == DataGridEditAction.Cancel && e.Row.Item is DataRowView cancelled)
                Grid.Dispatcher.BeginInvoke(() => RowValuesChanged?.Invoke(cancelled.Row), DispatcherPriority.Background);
            if (_searchPending)
                Grid.Dispatcher.BeginInvoke(ApplySearchNow, DispatcherPriority.Background);
        }

        /// <summary>Runs once the grid has committed the row (the event comes before the values are in the row).</summary>
        private void AfterCommit(Action action)
        {
            _pendingEdits++;
            Grid.Dispatcher.BeginInvoke(() =>
            {
                _pendingEdits--;
                action();
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// Records the row edits that have just ended, before anything that reads the history or the changes (undo,
        /// save, closing): a key pressed right after Enter can come before the posted recording, and Ctrl+Z would then
        /// undo the change before this one.
        /// </summary>
        private void FlushEdits()
        {
            while (_pendingEdits > 0)
            {
                var before = _pendingEdits;
                // Runs the queued operations up to Background priority, the recordings among them
                Grid.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                if (_pendingEdits == before)
                    break;   // not run (shouldn't happen): don't loop
            }
        }

        private void RefuseEdit(DataGridRowEditEndingEventArgs e, string problem)
        {
            e.Cancel = true;
            _editError = problem;
            if (!_quietEdit)
                Grid.Dispatcher.BeginInvoke(ShowEditError, DispatcherPriority.Background);
        }

        private bool IsEmpty(DataRowView row) =>
            Table!.Columns.Cast<DataColumn>().All(c => row[c.Ordinal] is DBNull or "" or false || Equals(row[c.Ordinal], c.DefaultValue) || IsReadOnlyColumn(c.ColumnName));

        private void ShowEditError()
        {
            if (_editError is not { } error)
                return;
            _editError = null;
            AppDialog.Error(Window.GetWindow(View), "Edit row", "This row can't be kept as it is",
                $"{error}\n\nCorrect it and press Enter, or press Esc to undo your changes to the row.");
            Grid.Focus();
        }

        private DataColumn? PartNumberColumn => Table == null ? null : AccessDatabase.FindColumn(Table, "Part Number");

        /// <summary>
        /// Why a row's values can't be kept, or null: a text longer than its field, or (when the row is new or its
        /// Part Number changed) an empty Part Number or one that another row has. Altium finds a component by its Part
        /// Number, so each needs its own (compared ignoring case, as Access and Altium do). Rows whose Part Number
        /// isn't changed are kept as they are, even if the table already had them twice. Checked when a row is left
        /// and again when saving (when the database is checked too).
        /// </summary>
        private string? RowProblem(DataRow row, Func<DataColumn, object> value, bool partNumberChanged)
        {
            foreach (var column in EditableColumns())
                if (value(column) is string text && FieldOf(column.ColumnName) is { Type: FieldType.Text } field && text.Length > field.Size)
                    return $"\"{column.ColumnName}\" takes at most {field.Size} characters; this value has {text.Length}.";

            if (!partNumberChanged || PartNumberColumn is not { } pn || pn.DataType != typeof(string))
                return null;
            var partNumber = (value(pn) as string)?.Trim();
            if (string.IsNullOrEmpty(partNumber))
                return $"\"{pn.ColumnName}\" can't be empty: Altium finds the component by it.";
            bool taken;
            if (_partNumbers != null)
            {
                var own = row.RowState is not (DataRowState.Deleted or DataRowState.Detached) &&
                          string.Equals((row[pn] as string)?.Trim(), partNumber, StringComparison.OrdinalIgnoreCase);
                taken = _partNumbers.GetValueOrDefault(partNumber) > (own ? 1 : 0);
            }
            else
                taken = Table!.Rows.Cast<DataRow>().Any(r => r != row && r.RowState is not (DataRowState.Deleted or DataRowState.Detached) &&
                                                             string.Equals((r[pn] as string)?.Trim(), partNumber, StringComparison.OrdinalIgnoreCase));
            return taken ? $"Another row already has the {pn.ColumnName} \"{partNumber}\". Each component needs its own." : null;
        }

        // While many rows are checked at once: how many rows have each Part Number, so that each check is a lookup and
        // not a pass over the table (thousands of pasted part numbers would take minutes)
        private Dictionary<string, int>? _partNumbers;

        private Dictionary<string, int>? CountPartNumbers()
        {
            if (Table == null || PartNumberColumn is not { } pn)
                return null;
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (DataRow row in Table.Rows)
                if (row.RowState is not (DataRowState.Deleted or DataRowState.Detached) && (row[pn] as string)?.Trim() is { } name)
                    counts[name] = counts.GetValueOrDefault(name) + 1;
            return counts;
        }

        private bool PartNumberChanged(DataRow row) => PartNumberColumn is { } pn &&
            (row.RowState == DataRowState.Added ||
             row.RowState == DataRowState.Modified && !Equals(row[pn, DataRowVersion.Original], row[pn, DataRowVersion.Current]));

        /// <summary>"RES 10K", "RES 10K (2)" → the first free "RES 10K (n)" from 2 up, within the field's size.</summary>
        private string UniqueCopyName(string name, DataColumn column)
        {
            var match = System.Text.RegularExpressions.Regex.Match(name, @"^(?<base>.*?) \((?<n>\d+)\)$");
            var stem = match.Success ? match.Groups["base"].Value : name;
            var used = Table!.Rows.Cast<DataRow>()
                .Where(r => r.RowState is not (DataRowState.Deleted or DataRowState.Detached) && r[column] is string)
                .Select(r => ((string)r[column]).Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var size = FieldOf(column.ColumnName) is { Type: FieldType.Text } field ? field.Size : int.MaxValue;
            for (var n = 2; ; n++)
            {
                var suffix = $" ({n})";
                var candidate = (stem.Length + suffix.Length > size ? stem[..Math.Max(size - suffix.Length, 0)] : stem) + suffix;
                if (!used.Contains(candidate))
                    return candidate;
            }
        }

        /// <summary>
        /// Ends the row being edited, if any, before something that needs it ended (saving, undoing, closing, reloading,
        /// switching tabs). If its values can't be kept, asks whether to drop its changes. False: the user keeps
        /// editing, and the caller stops.
        /// </summary>
        public bool TryFinishEditing(Window? owner)
        {
            // A value being typed elsewhere for this table (the details) is applied first, so it isn't left out
            FinishingEdits?.Invoke();
            if (!IsRowEditing)
            {
                FlushEdits();
                return true;
            }

            _editError = null;
            _quietEdit = true;
            try
            {
                if (Grid.CommitEdit(DataGridEditingUnit.Cell, true))
                    Grid.CommitEdit(DataGridEditingUnit.Row, true);
            }
            finally
            {
                _quietEdit = false;
            }
            if (!IsRowEditing)
            {
                // The committed row's history step is recorded after the commit; now, so a save includes it
                FlushEdits();
                return true;
            }

            var error = _editError ?? "A value doesn't fit its field (the cell marked in red).";
            _editError = null;
            if (!AppDialog.ConfirmDestructive(owner, "Edit row", $"A row in \"{Name}\" can't be kept as it is", "Discard changes",
                    $"{error}\n\nDiscard the changes to this row, or keep editing it?"))
                return false;
            CancelRowEdit();
            return true;
        }

        private void CancelRowEdit()
        {
            Grid.CancelEdit(DataGridEditingUnit.Cell);
            Grid.CancelEdit(DataGridEditingUnit.Row);
            if (Grid.Items is IEditableCollectionView view)
            {
                if (view.IsAddingNew)
                    view.CancelNew();
                else if (view.IsEditingItem)
                    view.CancelEdit();
            }
        }

        #endregion

        #region Adding and deleting rows

        /// <summary>
        /// Starts a new row in the empty row at the end, in its first column; with <paramref name="duplicate"/> it
        /// starts as a copy of the selected row (except AutoNumber values) with a numbered Part Number of its own.
        /// Kept like any edit, when it is left.
        /// </summary>
        public void AddRow(bool duplicate)
        {
            if (!_isEditing || Table == null)
                return;
            var source = duplicate ? SelectedRow?.Row : null;
            if (duplicate && source == null)
                return;
            if (!TryFinishEditing(Window.GetWindow(View)))
                return;
            // The selected row was a new one left empty, dropped just now
            if (source is { RowState: DataRowState.Detached or DataRowState.Deleted })
                return;
            // The new row has to be visible: leave the search
            if (_filtered)
            {
                Query = "";
                ApplySearchNow();
            }
            UpdateEditing();

            var column = Grid.Columns.Where(c => !c.IsReadOnly && c.Visibility == Visibility.Visible)
                .OrderBy(c => c.DisplayIndex).FirstOrDefault();
            if (column == null)
                return;
            Grid.UnselectAllCells();
            Grid.ScrollIntoView(CollectionView.NewItemPlaceholder, column);
            Grid.UpdateLayout();
            Grid.CurrentCell = new DataGridCellInfo(CollectionView.NewItemPlaceholder, column);
            Grid.Focus();
            if (!Grid.BeginEdit())
                return;

            if (source != null && Grid.Items is IEditableCollectionView { CurrentAddItem: DataRowView added })
            {
                foreach (var c in EditableColumns())
                    added[c.Ordinal] = source[c];
                // The copy gets a Part Number of its own straight away, numbered so it is told apart from the original
                if (PartNumberColumn is { } partNumber && source[partNumber] is string original && original.Trim().Length > 0)
                    added[partNumber.Ordinal] = UniqueCopyName(original.Trim(), partNumber);
                // The row's cells were made before the copy and don't hear about values set on a row being added: they
                // are refreshed (cells made later, when scrolled to, read the copied values themselves). The cell being
                // edited gets its text selected, to be typed over.
                if (Grid.ItemContainerGenerator.ContainerFromItem(added) is DataGridRow row)
                {
                    foreach (var gridColumn in Grid.Columns)
                    {
                        switch (gridColumn.GetCellContent(row))
                        {
                            case TextBox box:
                                box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
                                if (gridColumn == column)
                                    box.SelectAll();
                                break;
                            case TextBlock text:
                                text.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
                                break;
                            case System.Windows.Controls.Primitives.ToggleButton check:
                                check.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateTarget();
                                break;
                        }
                    }
                }
                Status?.Invoke($"Copy of {RowTitle(source)} as {RowTitle(added.Row)}: change what differs, then press Enter");
            }
            else
                Status?.Invoke("New row: fill it in and press Enter, or Esc to drop it");
        }

        /// <summary>Deletes the selected rows from the table (from the database when it is saved; Ctrl+Z brings them
        /// back), as one step of the history.</summary>
        public void DeleteSelectedRows()
        {
            if (!_isEditing || Table == null)
                return;
            // A new row that isn't kept yet is just dropped
            if (Grid.Items is IEditableCollectionView { IsAddingNew: true } && SelectedRows().Count == 0)
            {
                CancelRowEdit();
                return;
            }
            if (!TryFinishEditing(Window.GetWindow(View)))
                return;
            var rows = SelectedRows();
            if (rows.Count == 0 || Editing.ConfirmDelete && !AppDialog.ConfirmDestructive(Window.GetWindow(View), "Delete rows",
                    rows.Count == 1 ? $"Delete {RowTitle(rows[0])}?" : $"Delete {rows.Count} rows?", "Delete", "They leave the database when the table is saved; Ctrl+Z brings them back."))
                return;

            var order = ShownOrder();
            var index = rows.Min(r => order.GetValueOrDefault(r));
            var description = rows.Count == 1 ? $"Delete row {RowTitle(rows[0])}" : $"Delete {rows.Count} rows";
            var steps = rows.Select(r => new RowStep(r, Snapshot(r, DataRowVersion.Current), RowSnapshot.Absent)).ToList();
            foreach (var step in steps)
                Apply(step.Row, step.After);
            Record(description, steps);

            // The row that took the place of the first deleted one is selected, so Del can go on down the table
            var at = Math.Clamp(index, 0, Math.Max(Grid.Items.Count - 1, 0));
            if (Grid.Items.Count > 0 && (Grid.Items[at] as DataRowView ?? (at > 0 ? Grid.Items[at - 1] as DataRowView : null)) is { } next)
            {
                SelectItem(next);
                Grid.ScrollIntoView(next);
            }
            else
                SelectItem(null);
        }

        public void Reload() => ReloadRequested?.Invoke();

        #endregion
    }
}
