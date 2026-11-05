using System.Data;
using System.Windows;
using System.Windows.Input;
using Altium_Library_Manager.Services;
using Altium_Library_Manager.Services.Templates;

namespace Altium_Library_Manager
{
    /// <summary>A row's values at one moment, or that it wasn't in the table (added later, or deleted).</summary>
    public sealed record RowSnapshot(bool Present, object[] Values)
    {
        public static readonly RowSnapshot Absent = new(false, []);

        public bool SameAs(RowSnapshot other) =>
            Present == other.Present && Values.Length == other.Values.Length &&
            Values.Zip(other.Values).All(p => p.First is byte[] a && p.Second is byte[] b ? a.AsSpan().SequenceEqual(b) : Equals(p.First, p.Second));
    }

    /// <summary>One row's part of a change: how it was before and after.</summary>
    public sealed record RowStep(DataRow Row, RowSnapshot Before, RowSnapshot After);

    /// <summary>
    /// A table's changes and their history. Every change (a row edited in the grid, rows added or deleted, and later
    /// edits from other places, which go through <see cref="ChangeRow"/>) is made in the loaded table and recorded as
    /// a step that Undo / Redo (Ctrl+Z / Ctrl+Y) take back or make again. Nothing reaches the database until
    /// <see cref="Save"/>, which writes all of the table's changes in one transaction; until then the tab's title
    /// has a star. What is pending is what the rows' states say (added, modified, deleted), so undoing a change back
    /// to how the table was saved leaves nothing to save, and undoing past a save makes it pending again.
    /// </summary>
    public sealed partial class TableTab
    {
        private sealed record Change(string Description, IReadOnlyList<RowStep> Steps);

        // About 100 MB for a table of 60 columns: each row keeps its values from before and after
        private const int MaxHistoryRows = 100_000;
        private readonly List<Change> _undo = [];
        private readonly List<Change> _redo = [];
        private bool _isDirty;

        /// <summary>The table has changes that aren't saved to the database.</summary>
        public bool IsDirty => _isDirty;

        /// <summary>Unsaved changes, or a row being edited or just ended: what reading the table again would lose.</summary>
        public bool HasPendingChanges => _isDirty || IsRowEditing || _pendingEdits > 0;

        public bool CanUndo => _undo.Count > 0;

        public bool CanRedo => _redo.Count > 0;

        public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;

        public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

        private RowSnapshot Snapshot(DataRow row, DataRowVersion version) =>
            new(true, Table!.Columns.Cast<DataColumn>().Select(c => row[c, version]).ToArray());

        private void Record(string description, IReadOnlyList<RowStep> steps)
        {
            _undo.Add(new Change(description, steps));
            // One step can hold a whole column's rows, so the rows kept are limited too (the last change always stays)
            var rows = _undo.Sum(c => c.Steps.Count);
            while (_undo.Count > 1 && (_undo.Count > Editing.UndoSteps || rows > MaxHistoryRows))
            {
                rows -= _undo[0].Steps.Count;
                _undo.RemoveAt(0);
            }
            _redo.Clear();
            HistoryChanged();
            Status?.Invoke($"{description}: not saved yet (Ctrl+S saves, Ctrl+Z undoes)");
        }

        /// <summary>
        /// Changes fields of a row from outside the grid (the Details panel), as one step of the history. Returns why
        /// the values can't be kept, or null.
        /// </summary>
        public string? ChangeRow(DataRow row, IReadOnlyDictionary<string, object> values)
        {
            if (Table == null || row.Table != Table || row.RowState is DataRowState.Deleted or DataRowState.Detached)
                return "The row is no longer in the table.";
            if (!TryFinishEditing(Window.GetWindow(View)))
                return "Another row is being edited.";

            var before = Snapshot(row, DataRowVersion.Current);
            var after = (object[])before.Values.Clone();
            foreach (var (name, value) in values)
                if (Table.Columns[name] is { } column && !IsReadOnlyColumn(column.ColumnName))
                    after[column.Ordinal] = value is string { Length: 0 } && FieldOf(column.ColumnName) is not { Required: true } ? DBNull.Value : value;
            var pn = PartNumberColumn;
            var partNumberChanged = pn != null && !Equals(after[pn.Ordinal], before.Values[pn.Ordinal]);
            if (RowProblem(row, c => after[c.Ordinal], partNumberChanged) is { } problem)
                return problem;

            var snapshot = new RowSnapshot(true, after);
            if (snapshot.SameAs(before))
                return null;
            Apply(row, snapshot);
            Record($"Edit row {RowTitle(row)}", [new RowStep(row, before, snapshot)]);
            return null;
        }

        /// <summary>Whether a field's value can be typed in (outside the grid): Edit is on and Access doesn't fill it itself.</summary>
        public bool CanChangeField(string column) =>
            _isEditing && Table?.Columns[column] is { } c && !IsReadOnlyColumn(c.ColumnName);

        /// <summary>
        /// Changes one field of a row to typed text (the Details panel), read as the field's type (a number, a date, …
        /// as written in this computer's language; empty clears the field), as one step of the history. Returns why
        /// it can't be, or null.
        /// </summary>
        public string? ChangeField(DataRow row, string columnName, string text)
        {
            if (Table?.Columns[columnName] is not { } column)
                return $"The table has no field \"{columnName}\".";
            if (!CanChangeField(column.ColumnName))
                return _isEditing ? $"\"{column.ColumnName}\" is filled in by Access." : "Turn on Edit (Ctrl+E) to change values.";

            return ParseField(column, text, out var value) ?? ChangeRow(row, new Dictionary<string, object> { [column.ColumnName] = value });
        }

        /// <summary>Reads typed text as a field's type. Returns why it can't be, or null. Empty text is an empty field
        /// (for a text field it stays "", which the caller keeps only if the field is required), and No for a Yes/No
        /// field, which Access can't leave empty.</summary>
        private static string? ParseField(DataColumn column, string text, out object value)
        {
            value = DBNull.Value;
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            var type = column.DataType;
            if (type == typeof(string))
                value = text;
            else if (text.Trim().Length == 0)
                value = type == typeof(bool) ? false : DBNull.Value;
            else if (type == typeof(bool))
            {
                var t = text.Trim().ToLowerInvariant();
                if (t is "1" or "yes" or "true" or "да")
                    value = true;
                else if (t is "0" or "no" or "false" or "нет")
                    value = false;
                else
                    return $"\"{column.ColumnName}\" is a Yes/No field: type yes or no.";
            }
            else if (type == typeof(Guid))
            {
                if (!Guid.TryParse(text.Trim(), out var guid))
                    return $"\"{column.ColumnName}\" takes a GUID, like {{12345678-90AB-CDEF-1234-567890ABCDEF}}.";
                value = guid;
            }
            else if (type == typeof(DateTime))
            {
                if (!DateTime.TryParse(text.Trim(), culture, System.Globalization.DateTimeStyles.None, out var date))
                    return $"\"{column.ColumnName}\" takes a date, like {DateTime.Now.ToString(culture)}.";
                value = date;
            }
            else
            {
                try
                {
                    value = Convert.ChangeType(text.Trim(), type, culture);
                    // double.Parse takes "NaN" and "Infinity" too, and an overflow as infinity; a field can't hold those
                    if (value is double.NaN or double.PositiveInfinity or double.NegativeInfinity or float.NaN or float.PositiveInfinity or float.NegativeInfinity)
                        throw new OverflowException();
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
                {
                    return $"\"{column.ColumnName}\" takes a number (\"{text.Trim()}\" isn't one, or is too large).";
                }
            }
            return null;
        }

        /// <summary>Ctrl+Z: takes back the last change.</summary>
        public void Undo() => Step(_undo, _redo, undo: true);

        /// <summary>Ctrl+Y: makes the last undone change again.</summary>
        public void Redo() => Step(_redo, _undo, undo: false);

        private void Step(List<Change> from, List<Change> to, bool undo)
        {
            // Finishing first: the edit that has just ended may be the only change, not recorded yet
            if (Table == null || !TryFinishEditing(Window.GetWindow(View)) || from.Count == 0)
                return;
            var change = from[^1];
            from.RemoveAt(from.Count - 1);
            var steps = undo ? change.Steps.Reverse() : change.Steps;
            foreach (var step in steps)
                Apply(step.Row, undo ? step.Before : step.After);
            to.Add(change);

            // The rows it brought back or changed are selected (deleted ones simply go)
            var shown = change.Steps.Select(s => s.Row).Where(r => r.RowState is not (DataRowState.Deleted or DataRowState.Detached)).ToHashSet();
            var items = Grid.Items.OfType<DataRowView>().Where(v => shown.Contains(v.Row)).ToList();
            if (items.Count > 0)
            {
                SelectItems(items);
                Grid.ScrollIntoView(items[0]);
            }
            HistoryChanged();
            Status?.Invoke(undo ? $"Undone: {change.Description}" : $"Redone: {change.Description}");
        }

        /// <summary>
        /// Puts a row into the given state: takes it out of the table (an added row is dropped, a saved one is marked
        /// deleted), or gives it these values, bringing it back first if it was taken out. AutoNumber and binary fields
        /// are left as they are.
        /// </summary>
        private void Apply(DataRow row, RowSnapshot state)
        {
            if (!state.Present)
            {
                if (row.RowState == DataRowState.Added)
                    Table!.Rows.Remove(row);
                else if (row.RowState is DataRowState.Unchanged or DataRowState.Modified)
                    row.Delete();
                return;
            }

            // A deleted row comes back as it was saved, then gets the values it had
            if (row.RowState == DataRowState.Deleted)
                row.RejectChanges();
            // A dropped row (an added one undone) has no values to compare: all are set, and it is added again
            var detached = row.RowState == DataRowState.Detached;
            row.BeginEdit();
            foreach (var column in EditableColumns())
                if (detached || !Equals(row[column], state.Values[column.Ordinal]))
                    row[column] = state.Values[column.Ordinal];
            row.EndEdit();
            if (detached)
                Table!.Rows.Add(row);
            Normalize(row);
        }

        /// <summary>A row changed back to exactly how it was saved has nothing to save.</summary>
        private static void Normalize(DataRow row)
        {
            if (row.RowState == DataRowState.Modified &&
                row.Table.Columns.Cast<DataColumn>().All(c => Equals(row[c, DataRowVersion.Original], row[c, DataRowVersion.Current])))
                row.AcceptChanges();
        }

        private void HistoryChanged()
        {
            UpdateDirty();
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            OnPropertyChanged(nameof(UndoDescription));
            OnPropertyChanged(nameof(RedoDescription));
            UpdateCount();
            RowsChanged?.Invoke();
        }

        private void UpdateDirty()
        {
            var dirty = Table != null && Table.Rows.Cast<DataRow>().Any(r => r.RowState != DataRowState.Unchanged);
            if (dirty == _isDirty)
                return;
            _isDirty = dirty;
            OnPropertyChanged(nameof(IsDirty));
        }

        private void ClearHistory()
        {
            _undo.Clear();
            _redo.Clear();
            HistoryChanged();
        }

        #region Saving

        /// <summary>
        /// Ctrl+S: writes the table's changes to the database, all in one transaction. The changed rows are checked
        /// first (field sizes, Part Numbers, including against rows added to the database elsewhere); on any problem,
        /// or if the database refuses something, nothing is written and the changes stay. True when saved (or when
        /// there was nothing to save).
        /// </summary>
        public bool Save(Window? owner)
        {
            if (!TryFinishEditing(owner))
                return false;
            UpdateDirty();
            if (!_isDirty)
                return true;
            if (Table == null || Database == null || Fields == null)
                return false;

            var changed = Table.Rows.Cast<DataRow>().Where(r => r.RowState != DataRowState.Unchanged).ToList();
            var problems = new List<(DataRow Row, string Problem)>();
            Mouse.OverrideCursor = Cursors.Wait;
            _partNumbers = CountPartNumbers();
            try
            {
                foreach (var row in changed.Where(r => r.RowState is DataRowState.Added or DataRowState.Modified))
                    if (RowProblem(row, c => row[c], PartNumberChanged(row)) is { } problem)
                        problems.Add((row, problem));
                if (problems.Count == 0)
                    problems.AddRange(PartNumbersTakenInDatabase(changed));
            }
            catch (Exception ex)
            {
                problems.Add((changed[0], ex.Message));
            }
            finally
            {
                _partNumbers = null;
                Mouse.OverrideCursor = null;
            }
            if (problems.Count > 0)
            {
                SelectRow(problems[0].Row);
                var list = problems.Take(8).Select(p => $"{RowTitle(p.Row)}: {p.Problem}").ToList();
                if (problems.Count > list.Count)
                    list.Add($"and {problems.Count - list.Count} more");
                AppDialog.Error(owner, "Save", $"Couldn't save \"{Name}\"",
                    $"{string.Join("\n", list)}\n\nNothing was saved; correct the rows (or undo the changes) and save again.");
                return false;
            }

            var autoNumber = Fields.FirstOrDefault(f => f.Type == FieldType.AutoNumber) is { } auto ? Table.Columns[auto.Name] : null;
            var added = changed.Where(r => r.RowState == DataRowState.Added).ToList();
            var writes = new List<RowWrite>();
            foreach (var row in changed.Where(r => r.RowState == DataRowState.Deleted))
                writes.Add(new RowWrite(RowWriteKind.Delete, RowTitle(row), FindBy(row, DataRowVersion.Original),
                    WholeIfNoKey(row, DataRowVersion.Original), []));
            foreach (var row in changed.Where(r => r.RowState == DataRowState.Modified))
                writes.Add(new RowWrite(RowWriteKind.Update, RowTitle(row), FindBy(row, DataRowVersion.Original),
                    WholeIfNoKey(row, DataRowVersion.Original),
                    EditableColumns().Where(c => !Equals(row[c, DataRowVersion.Original], row[c, DataRowVersion.Current]))
                        .Select(c => new FieldValue(c, row[c])).ToList()));
            foreach (var row in added)
                writes.Add(new RowWrite(RowWriteKind.Insert, RowTitle(row), [], null,
                    EditableColumns().Select(c => new FieldValue(c, row[c])).ToList()));

            List<object?> ids;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                ids = Database.SaveChanges(Name, writes, autoNumber);
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                AppDialog.Error(owner, "Save", $"Couldn't save \"{Name}\"",
                    $"{ex.Message}\n\nNothing was saved; your changes are still here. Reload the table (F5) to see what the database has now.");
                return false;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            // The AutoNumber values Access gave the new rows
            if (autoNumber != null)
                for (var i = 0; i < added.Count && i < ids.Count; i++)
                    if (ids[i] is { } id and not DBNull)
                        added[i][autoNumber] = Convert.ChangeType(id, autoNumber.DataType);
            Table.AcceptChanges();
            HistoryChanged();
            Status?.Invoke(changed.Count == 1 ? $"Saved 1 changed row to {Name}" : $"Saved {changed.Count} changed rows to {Name}");
            return true;
        }

        /// <summary>New or changed Part Numbers that rows in the database already have (added there since the table
        /// was read), except rows this save deletes or renames.</summary>
        private IEnumerable<(DataRow, string)> PartNumbersTakenInDatabase(List<DataRow> changed)
        {
            if (PartNumberColumn is not { } pn || pn.DataType != typeof(string))
                yield break;
            static bool Same(object a, object b) =>
                a is string x && b is string y && string.Equals(x.Trim(), y.Trim(), StringComparison.OrdinalIgnoreCase);

            foreach (var row in changed.Where(r => r.RowState is DataRowState.Added or DataRowState.Modified && PartNumberChanged(r)))
            {
                var value = row[pn];
                // Saved rows with this Part Number that this save takes it from
                var freed = changed.Count(r => r.RowState != DataRowState.Added && Same(r[pn, DataRowVersion.Original], value) &&
                                               (r.RowState == DataRowState.Deleted || !Same(r[pn, DataRowVersion.Current], value)));
                if (Database!.CountRowsWith(Name, new FieldValue(pn, value)) > freed)
                    yield return (row, $"a row in the database already has the {pn.ColumnName} \"{value}\" (added since the table was read).");
            }
        }

        /// <summary>What the row is found by in the database: its primary key, or (without one) all its values except
        /// long texts and binary data, which Access can't compare.</summary>
        private List<FieldValue> FindBy(DataRow row, DataRowVersion version)
        {
            var columns = Fields!.Where(f => f.PrimaryKey).Select(f => Table!.Columns[f.Name]).OfType<DataColumn>().ToList();
            if (columns.Count == 0)
                columns = FieldColumns(Table!)
                    .Where(c => c.DataType != typeof(byte[]) && FieldOf(c.ColumnName)?.Type != FieldType.LongText)
                    .ToList();
            return columns.Select(c => new FieldValue(c, row[c, version])).ToList();
        }

        /// <summary>Without a key, the row's every value (as saved), so that one of several identical rows can still
        /// be changed or deleted (see <see cref="AccessDatabase.SaveChanges"/>); with a key, null.</summary>
        private List<FieldValue>? WholeIfNoKey(DataRow row, DataRowVersion version) => Fields!.Any(f => f.PrimaryKey)
            ? null
            : FieldColumns(Table!).Select(c => new FieldValue(c, row[c, version])).ToList();

        /// <summary>Drops the unsaved changes: the table is as it was last saved, and the history is cleared.</summary>
        public void Discard()
        {
            if (Table == null)
                return;
            CancelRowEdit();
            Table.RejectChanges();
            _rowText.Clear();
            ClearHistory();
        }

        /// <summary>
        /// Before the tab closes (or the app, or the table is read again): with unsaved changes, asks whether to save
        /// them. False: the user cancelled, or the save failed, and the caller stops.
        /// </summary>
        public bool ConfirmClose(Window? owner)
        {
            if (!TryFinishEditing(owner))
                return false;
            UpdateDirty();
            if (!_isDirty)
                return true;
            switch (AppDialog.Show(owner, DialogKind.Question, "Unsaved changes", $"Save the changes to \"{Name}\"?",
                        "They aren't in the database yet. Without saving, they are lost.", "Save", "Don't save", "Cancel"))
            {
                case 0:
                    return Save(owner);
                case 1:
                    Discard();
                    return true;
                default:
                    return false;
            }
        }

        private void SelectRow(DataRow row)
        {
            if (Grid.Items.OfType<DataRowView>().FirstOrDefault(v => v.Row == row) is { } item)
            {
                SelectItem(item);
                Grid.ScrollIntoView(item);
            }
        }

        #endregion
    }
}
