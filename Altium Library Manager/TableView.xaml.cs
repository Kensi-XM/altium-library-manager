using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Altium_Library_Manager.Services;

namespace Altium_Library_Manager
{
    /// <summary>
    /// A table tab's content: the toolbar (row search, the columns it looks in, the row count; Edit with its row
    /// commands) above the tab's grid. The search and editing themselves are in <see cref="TableTab"/>.
    /// </summary>
    public partial class TableView : UserControl
    {
        private readonly TableTab _tab;

        public TableView(TableTab tab)
        {
            InitializeComponent();
            _tab = tab;
            DataContext = tab;
            GridHost.Content = tab.Grid;
            tab.Grid.PreviewKeyDown += Grid_PreviewKeyDown;
            tab.Grid.PreviewMouseRightButtonDown += Grid_PreviewMouseRightButtonDown;
            tab.Grid.ContextMenu = new ContextMenu();
            tab.Grid.ContextMenuOpening += Grid_ContextMenuOpening;
        }

        private DataGrid Grid => _tab.Grid;

        /// <summary>Ctrl+F: the cursor goes to the search box (once the tab is shown, if it was just activated).</summary>
        public void FocusSearch()
        {
            if (!IsLoaded)
            {
                Dispatcher.BeginInvoke(FocusSearch, DispatcherPriority.Loaded);
                return;
            }
            SearchBox.Focus();
            SearchBox.SelectAll();
        }

        #region Search box

        // Typing a search while a row is being edited: the row is saved first (the rows shown are about to change)
        private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
            _tab.TryFinishEditing(Window.GetWindow(this));

        /// <summary>
        /// The search box drives the table from the keyboard, as the table filter drives the table list: Up / Down
        /// (and Page Up / Down) move the selection while the cursor stays in the box, Enter goes into the table at the
        /// selected row, Esc clears the search (or, when it is empty, goes back to the table).
        /// </summary>
        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down or Key.Up or Key.PageDown or Key.PageUp when Keyboard.Modifiers == ModifierKeys.None:
                    _tab.ApplySearchNow();
                    MoveSelection(e.Key switch { Key.Down => 1, Key.Up => -1, Key.PageDown => 10, _ => -10 });
                    e.Handled = true;
                    break;
                case Key.Enter:
                    _tab.ApplySearchNow();
                    FocusGrid();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    if (_tab.Query.Length > 0)
                    {
                        _tab.Query = "";
                        _tab.ApplySearchNow();
                    }
                    else
                        FocusGrid();
                    e.Handled = true;
                    break;
            }
        }

        private List<DataRowView> Rows() => Grid.Items.OfType<DataRowView>().ToList();

        private void MoveSelection(int delta)
        {
            var count = Grid.Items.Count;
            if (count == 0)
                return;
            var selected = _tab.SelectedRow is { } current ? Grid.Items.IndexOf(current) : -1;
            var index = selected < 0
                ? (delta > 0 ? 0 : count - 1)
                : Math.Clamp(selected + delta, 0, count - 1);
            // Not onto the empty row for a new row
            if (Grid.Items[index] is not DataRowView)
                index = Math.Max(index - 1, 0);
            if (Grid.Items[index] is not DataRowView row)
                return;
            _tab.SelectItem(row);
            Grid.ScrollIntoView(row);
        }

        /// <summary>Moves the keyboard into the table, onto the selected row (or the first one).</summary>
        private void FocusGrid()
        {
            var item = _tab.SelectedRow ?? Rows().FirstOrDefault();
            var column = Grid.CurrentColumn ?? Grid.Columns.Where(c => c.Visibility == Visibility.Visible).MinBy(c => c.DisplayIndex);
            if (item == null || column == null)
            {
                Grid.Focus();
                return;
            }
            _tab.SelectItem(item, column);
            Grid.ScrollIntoView(item, column);
            Grid.UpdateLayout();
            if (Grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row && column.GetCellContent(row)?.Parent is DataGridCell cell)
                cell.Focus();
            else
                Grid.Focus();
        }

        #endregion

        #region Search columns

        /// <summary>A menu of the table's columns to search in: "All columns", or any of them (checked; the menu stays
        /// open while picking several).</summary>
        private void ColumnsButton_Click(object sender, RoutedEventArgs e)
        {
            if (DropDown.WasJustClosed(ColumnsButton))
                return;
            var window = Window.GetWindow(this);
            // The window's font: otherwise the menu inherits the button's
            var menu = new ContextMenu
            {
                PlacementTarget = ColumnsButton,
                Placement = PlacementMode.Bottom,
                FontFamily = window?.FontFamily ?? FontFamily,
                FontSize = window?.FontSize ?? FontSize
            };
            var chosen = new HashSet<string>(_tab.SearchColumns, StringComparer.OrdinalIgnoreCase);
            var columns = Grid.Columns.OrderBy(c => c.DisplayIndex).Select(c => c.Header).OfType<string>().ToList();
            chosen.IntersectWith(columns);

            var all = new MenuItem { Header = "All columns", IsCheckable = true, IsChecked = chosen.Count == 0, StaysOpenOnClick = true };
            var items = columns.Select(name => new MenuItem
            {
                // TextBlock header: a plain string would turn '_' into an access key
                Header = new TextBlock { Text = name },
                Tag = name,
                IsCheckable = true,
                IsChecked = chosen.Contains(name),
                StaysOpenOnClick = true
            }).ToList();

            all.Click += (_, _) =>
            {
                foreach (var item in items)
                    item.IsChecked = false;
                all.IsChecked = true;
                _tab.SetSearchColumns([]);
            };
            foreach (var item in items)
            {
                item.Click += (_, _) =>
                {
                    var picked = items.Where(i => i.IsChecked).Select(i => (string)i.Tag).ToList();
                    all.IsChecked = picked.Count == 0;
                    _tab.SetSearchColumns(picked);
                };
            }

            menu.Items.Add(all);
            menu.Items.Add(new Separator());
            foreach (var item in items)
                menu.Items.Add(item);
            menu.IsOpen = true;
            DropDown.Track(menu, ColumnsButton);
        }

        #endregion

        #region Editing

        private void AddRow_Click(object sender, RoutedEventArgs e) => _tab.AddRow(duplicate: false);

        private void AddElements_Click(object sender, RoutedEventArgs e) => _tab.RequestAddElements();

        private void Duplicate_Click(object sender, RoutedEventArgs e) => _tab.AddRow(duplicate: true);

        private void Delete_Click(object sender, RoutedEventArgs e) => _tab.DeleteSelectedRows();

        private void Reload_Click(object sender, RoutedEventArgs e) => _tab.Reload();

        private void Save_Click(object sender, RoutedEventArgs e) => _tab.Save(Window.GetWindow(this));

        /// <summary>While editing: Del clears the selected cells, Ctrl+X / Ctrl+V cut and paste, Ctrl+Plus adds a row,
        /// Ctrl+D duplicates the selected one, Ctrl+Minus deletes the selected rows. (In a cell being typed in, these
        /// keys are the text box's.)</summary>
        private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox)
                return;
            var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
            if (ctrl && e.Key == Key.C)
            {
                _tab.CopySelectedCells();
                e.Handled = true;
                return;
            }
            if (!_tab.IsEditing)
                return;
            if (ctrl && e.Key is Key.OemPlus or Key.Add)
                _tab.AddRow(duplicate: false);
            else if (ctrl && e.Key == Key.D)
                _tab.AddRow(duplicate: true);
            else if (ctrl && e.Key is Key.OemMinus or Key.Subtract)
                _tab.DeleteSelectedRows();
            else if (ctrl && e.Key == Key.V)
                _tab.Paste();
            else if (ctrl && e.Key == Key.X)
                _tab.CutSelectedCells();
            else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Delete)
                _tab.ClearSelectedCells();
            else
                return;
            e.Handled = true;
        }

        #endregion

        #region Context menu

        private static T? Ancestor<T>(object? source) where T : DependencyObject
        {
            // Parts of text aren't visuals, whose parents can be asked for
            for (var current = source as DependencyObject; current is System.Windows.Media.Visual; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
                if (current is T found)
                    return found;
            return null;
        }

        /// <summary>A right click acts on the cell under it; inside the selection, it keeps the selection.</summary>
        private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Ancestor<DataGridCell>(e.OriginalSource) is { IsSelected: false, IsEditing: false, DataContext: DataRowView item } cell)
                _tab.SelectItem(item, cell.Column);
        }

        private void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var menu = Grid.ContextMenu;
            menu.Items.Clear();
            // The window's font: the grid's menu otherwise goes by its own defaults
            if (Window.GetWindow(this) is { } window)
            {
                menu.FontFamily = window.FontFamily;
                menu.FontSize = window.FontSize;
            }

            if (Ancestor<DataGridColumnHeader>(e.OriginalSource) is { Column: { } headerColumn })
                AddItem(menu, "Select column", null, true, () => _tab.SelectColumn(headerColumn));
            else if (Ancestor<DataGridCell>(e.OriginalSource) is { IsEditing: false, DataContext: DataRowView } cell)
                BuildCellMenu(menu, cell);
            else
                e.Handled = true;
        }

        private void BuildCellMenu(ContextMenu menu, DataGridCell cell)
        {
            var editing = _tab.IsEditing;
            var cells = Grid.SelectedCells.Count;

            // Paths and links, as in the details
            if (cells == 1 && _tab.Table is { } table && _tab.SelectedRow is { } view && cell.Column.Header is string name && table.Columns[name] is { } column)
            {
                var link = column.DataType == typeof(string) ? FieldLinks.Classify(name) : FieldLink.None;
                var value = Convert.ToString(view[column.Ordinal]) ?? "";
                if (FieldLinks.CanOpen(value, link))
                    AddItem(menu, "Open", null, true, () => _tab.RequestFieldAction(name, FieldAction.Open, cell));
                if (link != FieldLink.None)
                    AddItem(menu, FieldLinks.IsRef(link) ? "Choose from library…" : "Choose file…", null, editing,
                        () => _tab.RequestFieldAction(name, FieldAction.Browse, cell));
                if (menu.Items.Count > 0)
                    menu.Items.Add(new Separator());
            }

            AddItem(menu, "Cut", "Ctrl+X", editing, _tab.CutSelectedCells);
            AddItem(menu, "Copy", "Ctrl+C", true, () => _tab.CopySelectedCells());
            AddItem(menu, "Paste", "Ctrl+V", editing, _tab.Paste);
            AddItem(menu, cells == 1 ? "Clear cell" : $"Clear {cells} cells", "Del", editing, _tab.ClearSelectedCells);
            AddItem(menu, cells == 1 ? "Change value…" : $"Fill {cells} cells…", null, editing, _tab.FillSelectedCells);
            if (cells > 1)
                AddItem(menu, "Fill down", null, editing, _tab.FillDown);

            menu.Items.Add(new Separator());
            var rows = _tab.SelectedRows().Count;
            AddItem(menu, "Add row", "Ctrl+Plus", editing, () => _tab.AddRow(duplicate: false));
            AddItem(menu, "Add elements…", null, editing, _tab.RequestAddElements);
            AddItem(menu, "Duplicate row", "Ctrl+D", editing, () => _tab.AddRow(duplicate: true));
            AddItem(menu, rows <= 1 ? "Delete row" : $"Delete {rows} rows", "Ctrl+Minus", editing, _tab.DeleteSelectedRows);

            menu.Items.Add(new Separator());
            AddItem(menu, rows <= 1 ? "Select row" : "Select rows", null, true, _tab.SelectWholeRows);
            AddItem(menu, "Select column", null, true, () => _tab.SelectColumn(cell.Column));
        }

        private static void AddItem(ContextMenu menu, string text, string? gesture, bool enabled, Action action)
        {
            // TextBlock header: a plain string would turn '_' into an access key
            var item = new MenuItem { Header = new TextBlock { Text = text }, InputGestureText = gesture ?? "", IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        #endregion
    }
}
