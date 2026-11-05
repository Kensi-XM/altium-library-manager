using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Altium_Library_Manager.Services;

namespace Altium_Library_Manager
{
    /// <summary>
    /// The bin of deleted tables (see <see cref="DeletedTables"/>): restore a table into the open database, delete a
    /// copy for good, or open it in Access.
    /// </summary>
    public partial class DeletedTablesWindow : Window
    {
        private readonly AccessDatabase? _database;
        private readonly HashSet<string> _existingTables;
        // While a table is being restored the window stays open (Alt+F4 included)
        private bool _busy;

        public DeletedTablesWindow(AccessDatabase? database, IEnumerable<string> existingTables)
        {
            InitializeComponent();
            _database = database;
            _existingTables = new HashSet<string>(existingTables, StringComparer.OrdinalIgnoreCase);
            Refresh();
            Loaded += (_, _) => List.Focus();
        }

        /// <summary>Tables restored into the open database while the window was open (the main window lists them).</summary>
        public List<string> RestoredTables { get; } = [];

        private DeletedTable? Selected => List.SelectedItem as DeletedTable;

        private void Refresh(int select = 0)
        {
            List<DeletedTable> tables;
            try
            {
                tables = DeletedTables.List();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                tables = [];
                AppDialog.Error(this, "Deleted tables", "Couldn't read the deleted tables", ex.Message);
            }
            List.ItemsSource = tables;
            if (tables.Count > 0)
                List.SelectedIndex = Math.Clamp(select, 0, tables.Count - 1);
            EmptyText.Visibility = tables.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountText.Text = tables.Count == 0 ? "" : tables.Count == 1 ? "1 table" : $"{tables.Count} tables";
            UpdateButtons();
        }

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

        private void UpdateButtons()
        {
            var any = Selected != null;
            RestoreButton.IsEnabled = any && _database != null;
            RestoreButton.ToolTip = _database == null ? "Open a database first: the table is restored into it" : "Put the table back into the open database (Enter)";
            DeleteButton.IsEnabled = OpenButton.IsEnabled = any;
        }

        private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ItemsControl.ContainerFromElement(List, (DependencyObject)e.OriginalSource) is ListBoxItem)
                Restore();
        }

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Restore();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                DeleteSelected();
                e.Handled = true;
            }
        }

        private void Restore_Click(object sender, RoutedEventArgs e) => Restore();

        /// <summary>
        /// Puts the selected table back into the open database. Asks first when it was deleted from another database
        /// or its name is taken (it then gets a free name, e.g. "Resistors 0603 (2)").
        /// </summary>
        private async void Restore()
        {
            if (Selected is not { } table || _database is not { } database || _busy)
                return;

            var name = table.Table;
            for (var i = 2; _existingTables.Contains(name); i++)
                name = $"{table.Table} ({i})";
            var into = Path.GetFileName(database.FilePath);
            var notes = new List<string>();
            if (table.Database.Length > 0 && !string.Equals(table.Database, database.FilePath, StringComparison.OrdinalIgnoreCase))
                notes.Add($"It was deleted from {table.DatabaseName}, and goes into the open database, {into}.");
            if (name != table.Table)
                notes.Add($"{into} already has a table named \"{table.Table}\", so it is restored as \"{name}\".");
            if (notes.Count > 0 && !AppDialog.Confirm(this, "Restore table", $"Restore \"{table.Table}\" into {into}?",
                    "Restore", string.Join("\n\n", notes)))
                return;

            var index = List.SelectedIndex;
            _busy = true;
            IsEnabled = false;
            try
            {
                await Task.Run(() => DeletedTables.Restore(table, database, name));
            }
            catch (Exception ex)
            {
                AppDialog.Error(this, "Restore table", $"Couldn't restore \"{table.Table}\"", ex.Message);
                return;
            }
            finally
            {
                _busy = false;
                IsEnabled = true;
            }

            _existingTables.Add(name);
            RestoredTables.Add(name);
            Refresh(index);
            List.Focus();
        }

        private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

        private void DeleteSelected()
        {
            if (Selected is not { } table || _busy)
                return;
            if (!AppDialog.ConfirmDestructive(this, "Deleted tables", $"Delete the copy of \"{table.Table}\" for good?", "Delete",
                    $"Deleted {table.DeletedAt:g} from {table.DatabaseName}. Without the copy, the table can't be restored."))
                return;

            var index = List.SelectedIndex;
            try
            {
                DeletedTables.Delete(table);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppDialog.Error(this, "Deleted tables", "Couldn't delete the copy", ex.Message);
            }
            Refresh(index);
            List.Focus();
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is not { } table)
                return;
            try
            {
                Process.Start(new ProcessStartInfo(table.File) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
            {
                AppDialog.Error(this, "Deleted tables", "Couldn't open the copy", ex.Message);
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(DeletedTables.Folder);
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { DeletedTables.Folder } });
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
            {
                AppDialog.Error(this, "Deleted tables", "Couldn't open the folder", ex.Message);
            }
        }

        private void Window_Closing(object? sender, CancelEventArgs e) => e.Cancel = _busy;
    }
}
