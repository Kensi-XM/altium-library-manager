using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Windows.Threading;

namespace Altium_Library_Manager
{
    /// <summary>
    /// Row search: the words typed in the tab's search box, looked for in all columns or in the columns chosen for the
    /// table (remembered). The rows found are shown through a filtered view of the same table; the grid's columns,
    /// widths and sorting stay.
    /// </summary>
    public sealed partial class TableTab
    {
        // Between the fields of a row's search text, so a word never matches across two fields
        private const char FieldSeparator = '\u0001';

        // A short pause after typing before searching, so a fast typist's every key doesn't filter a large table
        private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

        // Each row's searched text, built when first needed (measured: searching 23,000 rows x 62 columns takes ~5 ms
        // this way, against ~300 ms for a DataView RowFilter with LIKE). A row's text is dropped when the row changes.
        private readonly Dictionary<DataRow, string> _rowText = [];

        private string _query = "";
        private bool _filtered;
        private bool _searchPending;
        private string _countText = "";

        private void InitializeSearch() => _searchTimer.Tick += (_, _) => ApplySearchNow();

        /// <summary>What is typed in the search box; applied after a short pause (or at once by <see cref="ApplySearchNow"/>).</summary>
        public string Query
        {
            get => _query;
            set
            {
                if (_query == value)
                    return;
                _query = value;
                OnPropertyChanged();
                _searchTimer.Stop();
                _searchTimer.Start();
            }
        }

        /// <summary>The rows shown are a search result (not the whole table).</summary>
        public bool IsFiltered => _filtered;

        /// <summary>"23,397 rows", or while searching "12 of 23,397 rows".</summary>
        public string CountText
        {
            get => _countText;
            private set
            {
                if (_countText == value)
                    return;
                _countText = value;
                OnPropertyChanged();
            }
        }

        /// <summary>The columns chosen for the search (as remembered, even if the table no longer has some); empty: all.</summary>
        public IReadOnlyList<string> SearchColumns => Columns.SearchColumns.TryGetValue(Name, out var names) ? names : [];

        /// <summary>Shown on the button that picks the search columns.</summary>
        public string SearchColumnsText
        {
            get
            {
                var chosen = ChosenSearchColumns();
                return chosen.Count switch
                {
                    0 => "All columns",
                    1 => chosen[0].ColumnName,
                    _ => $"{chosen.Count} columns"
                };
            }
        }

        /// <summary>Searches only these columns (none: all of them), remembered for this table.</summary>
        public void SetSearchColumns(IEnumerable<string> names)
        {
            var list = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0)
                Columns.SearchColumns.Remove(Name);
            else
                Columns.SearchColumns[Name] = list;
            SettingsChanged?.Invoke();

            _rowText.Clear();
            OnPropertyChanged(nameof(SearchColumns));
            OnPropertyChanged(nameof(SearchColumnsText));
            ApplySearchNow();
        }

        // The chosen columns the table has; empty when none are chosen (or none of them exists any more)
        private List<DataColumn> ChosenSearchColumns() => Table == null
            ? []
            : SearchColumns.Select(name => Table.Columns[name]).OfType<DataColumn>().ToList();

        /// <summary>Shows the rows that match the search (all rows when the box is empty), keeping the selected row
        /// when it is among them, otherwise selecting the first one found.</summary>
        public void ApplySearchNow()
        {
            _searchTimer.Stop();
            if (Table == null)
                return;
            // The rows can't be swapped under a row being edited; searched once it is saved or dropped
            if (IsRowEditing)
            {
                _searchPending = true;
                return;
            }
            _searchPending = false;

            var words = Services.SearchText.Words(_query);
            var chosen = ChosenSearchColumns();
            var columns = (chosen.Count > 0 ? chosen : FieldColumns(Table)).ToArray();
            var view = words.Count == 0
                ? Table.DefaultView
                : Table.AsEnumerable().Where(row => RowMatches(row, words, columns)).AsDataView();
            _filtered = words.Count > 0;
            ShowRows(view);
            UpdateEditing();
            UpdateCount();
            OnPropertyChanged(nameof(IsFiltered));
        }

        private bool RowMatches(DataRow row, IReadOnlyList<string> words, DataColumn[] columns)
        {
            if (row.RowState is DataRowState.Deleted or DataRowState.Detached)
                return false;
            if (!_rowText.TryGetValue(row, out var text))
                _rowText[row] = text = string.Join(FieldSeparator,
                    columns.Select(c => row.IsNull(c) ? "" : Convert.ToString(row[c], CultureInfo.CurrentCulture)));
            return Services.SearchText.Matches(text, words);
        }

        private void ShowRows(DataView view)
        {
            if (Grid.ItemsSource == view)
                return;

            var selected = SelectedRow?.Row;
            // A new items source drops the grid's sorting; it is put back on the new rows
            var sorts = Grid.Items.SortDescriptions.ToList();
            Grid.ItemsSource = view;
            using (Grid.Items.DeferRefresh())
            {
                Grid.Items.SortDescriptions.Clear();
                foreach (var sort in sorts)
                    Grid.Items.SortDescriptions.Add(sort);
            }
            foreach (var column in Grid.Columns)
            {
                var sort = sorts.FirstOrDefault(s => s.PropertyName == column.SortMemberPath);
                column.SortDirection = sort.PropertyName == null ? null : sort.Direction;
            }

            DataRowView? select = null;
            if (selected != null)
                select = Grid.Items.OfType<DataRowView>().FirstOrDefault(r => r.Row == selected);
            if (select == null && _filtered)
                select = Grid.Items.OfType<DataRowView>().FirstOrDefault();
            SelectItem(select);
            if (select != null)
                Grid.ScrollIntoView(select);
        }

        private void UpdateCount()
        {
            if (Table == null)
            {
                CountText = "";
                return;
            }
            var total = Table.DefaultView.Count;   // without deleted rows not saved yet
            var rows = total == 1 ? "row" : "rows";
            CountText = _filtered && Grid.ItemsSource is DataView view
                ? $"{view.Count:N0} of {total:N0} {rows}"
                : $"{total:N0} {rows}";
        }

        // The search text of a changed or deleted row is built again. RowChanging comes before the views re-check
        // the row, so they see its new text.
        private void Table_RowChanging(object sender, DataRowChangeEventArgs e) => _rowText.Remove(e.Row);

        private void AttachTable(DataTable table)
        {
            _rowText.Clear();
            table.RowChanging += Table_RowChanging;
            table.RowDeleting += Table_RowChanging;
            table.ColumnChanged += Table_ColumnChanged;   // see RowValuesChanged
        }

        private void DetachTable(DataTable table)
        {
            table.RowChanging -= Table_RowChanging;
            table.RowDeleting -= Table_RowChanging;
            table.ColumnChanged -= Table_ColumnChanged;
        }
    }
}
