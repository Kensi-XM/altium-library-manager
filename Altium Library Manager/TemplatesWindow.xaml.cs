using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Altium_Library_Manager.Services.Templates;
using Microsoft.Win32;

namespace Altium_Library_Manager
{
    /// <summary>
    /// Lists, edits, adds, deletes, imports and exports the table templates. The selected template is edited as a
    /// copy and saved to its file when another template is picked or the window closes (only if it is valid;
    /// otherwise the user chooses to fix it or discard the changes). Adding, deleting and importing take effect at once.
    /// </summary>
    public partial class TemplatesWindow : Window
    {
        private readonly TemplateStore _store = App.Templates;

        private TableTemplate? _selected;
        private TableTemplate? _working;
        private bool _dirty;
        private bool _showing;
        // Set while the list is changed from code, so its selection events are ignored
        private bool _updating;

        public TemplatesWindow(TableTemplate? select = null)
        {
            InitializeComponent();
            TemplateList.ItemsSource = _store.Templates;
            Select(select ?? _store.Templates.FirstOrDefault());
            Loaded += (_, _) => ReportLoadProblems();
        }

        private void ReportLoadProblems()
        {
            if (App.TemplateProblems.Count == 0)
                return;
            AppDialog.Warning(this, "Table templates", "Some files in the templates folder were skipped",
                string.Join("\n", App.TemplateProblems));
            App.TemplateProblems = [];
        }

        #region Selection and editing

        private void Select(TableTemplate? template)
        {
            _updating = true;
            TemplateList.SelectedItem = template;
            _updating = false;
            Show(template);
        }

        private void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updating)
                return;
            var next = TemplateList.SelectedItem as TableTemplate;
            if (next == _selected)
                return;
            if (!TryCommit())
            {
                // Stay on the template with the problem
                Select(_selected);
                return;
            }
            Select(next);
        }

        /// <summary>Shows a copy of the template for editing.</summary>
        private void Show(TableTemplate? template)
        {
            if (_working != null)
                _working.PropertyChanged -= Working_PropertyChanged;

            _selected = template;
            _working = template?.Clone();
            _showing = true;
            EditPanel.DataContext = _working;
            Editor.Fields = _working?.Fields;
            _showing = false;
            EditPanel.IsEnabled = _working != null;
            if (_working != null)
                _working.PropertyChanged += Working_PropertyChanged;
            _dirty = false;

            EmptyText.Visibility = _store.Templates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DuplicateButton.IsEnabled = DeleteButton.IsEnabled = ExportButton.IsEnabled = template != null;
            Validate();
        }

        private void Working_PropertyChanged(object? sender, PropertyChangedEventArgs e) => MarkEdited();

        private void Editor_Changed(object? sender, EventArgs e)
        {
            // Also raised when a template is shown (its field list is replaced)
            if (!_showing)
                MarkEdited();
        }

        private void MarkEdited()
        {
            _dirty = true;
            Validate();
        }

        private string? Problem()
        {
            if (_working == null)
                return null;
            var name = _working.Name.Trim();
            if (name.Length == 0)
                return "Template name is empty.";
            if (_store.Templates.Any(t => t != _selected && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                return $"Another template is already named \"{name}\".";
            return TemplateRules.CheckFields(_working.Fields).FirstOrDefault();
        }

        private void Validate()
        {
            var problem = Problem();
            ErrorText.Text = problem;
        }

        /// <summary>
        /// Saves the edited copy into the selected template. False when it has a problem and the user chose to go
        /// back and fix it.
        /// </summary>
        private bool TryCommit()
        {
            if (!_dirty || _selected == null || _working == null)
                return true;

            if (Problem() is { } problem)
            {
                var discard = AppDialog.Show(this, DialogKind.Warning, "Table templates", $"\"{_selected.Name}\" can't be saved",
                    $"{problem}\n\nGo back and fix it, or discard the changes to this template.", "Discard changes", "Keep editing") == 0;
                if (discard)
                    _dirty = false;
                return discard;
            }

            var template = _selected;
            var wasSelected = TemplateList.SelectedItem == template;
            try
            {
                SaveChanges(template, _working.Name.Trim(), _working.Description.Trim(), _working.Fields);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppDialog.Error(this, "Table templates", "Couldn't save the template", ex.Message);
                return false;
            }
            finally
            {
                // Saving re-sorts the list, which drops its selection
                if (wasSelected)
                {
                    _updating = true;
                    TemplateList.SelectedItem = template;
                    _updating = false;
                }
            }
            _dirty = false;
            return true;
        }

        /// <summary>
        /// Puts new contents into a stored template and saves it; if saving fails, the template keeps its old
        /// contents, so the list never shows what isn't on disk.
        /// </summary>
        private void SaveChanges(TableTemplate template, string name, string description, IEnumerable<TemplateField> fields)
        {
            var before = template.Clone();
            template.Name = name;
            template.Description = description;
            template.Fields = new ObservableCollection<TemplateField>(fields.Select(f => f.Clone()));
            _updating = true;
            try
            {
                _store.Save(template);
            }
            catch
            {
                template.Name = before.Name;
                template.Description = before.Description;
                template.Fields = before.Fields;
                throw;
            }
            finally
            {
                _updating = false;
            }
        }

        #endregion

        #region Commands

        private void New_Click(object sender, RoutedEventArgs e)
        {
            var template = BuiltInTemplates.Minimal();
            template.Name = "New template";
            template.Description = "";
            AddAndSelect(template);
        }

        private void Duplicate_Click(object sender, RoutedEventArgs e)
        {
            if (!TryCommit() || _selected == null)
                return;
            var copy = _selected.Clone();
            copy.Name = _selected.Name + " copy";
            AddAndSelect(copy);
        }

        private void AddAndSelect(TableTemplate template)
        {
            if (!TryCommit())
                return;
            try
            {
                _updating = true;
                _store.Add(template);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppDialog.Error(this, "Table templates", "Couldn't save the template", ex.Message);
                return;
            }
            finally
            {
                _updating = false;
            }
            Select(template);
            NameBox.Focus();
            NameBox.SelectAll();
        }

        private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

        private void DeleteSelected()
        {
            if (_selected == null)
                return;
            if (!AppDialog.Confirm(this, "Table templates", $"Delete the template \"{_selected.Name}\"?", "Delete",
                    "Tables already made from it don't change."))
                return;

            var index = _store.Templates.IndexOf(_selected);
            try
            {
                _updating = true;
                _store.Delete(_selected);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppDialog.Error(this, "Table templates", "Couldn't delete the template", ex.Message);
                return;
            }
            finally
            {
                _updating = false;
            }
            _dirty = false;
            Select(_store.Templates.Count == 0 ? null : _store.Templates[Math.Min(index, _store.Templates.Count - 1)]);
        }

        private void TemplateList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                DeleteSelected();
                e.Handled = true;
            }
            else if (e.Key == Key.F2 && _selected != null)
            {
                NameBox.Focus();
                NameBox.SelectAll();
                e.Handled = true;
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            if (!TryCommit())
                return;
            var dialog = new OpenFileDialog
            {
                Title = "Import table templates",
                Filter = "Table templates (*.json)|*.json|All files|*.*",
                Multiselect = true,
                InitialDirectory = ExchangeFolder
            };
            if (dialog.ShowDialog(this) != true)
                return;
            RememberExchangeFolder(dialog.FileName);

            var problems = new List<string>();
            TableTemplate? last = null;
            foreach (var path in dialog.FileNames)
            {
                try
                {
                    var template = TemplateStore.Read(path);
                    var existing = _store.Templates.FirstOrDefault(t => string.Equals(t.Name, template.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        var answer = AppDialog.Show(this, DialogKind.Question, "Import",
                            $"A template named \"{template.Name}\" already exists",
                            $"Replace it with the one from {Path.GetFileName(path)}, or keep both (the imported one gets a new name)?",
                            "Replace", "Keep both", "Skip");
                        if (answer == 2)
                            continue;
                        if (answer == 0)
                        {
                            SaveChanges(existing, existing.Name, template.Description, template.Fields);
                            last = existing;
                            continue;
                        }
                    }
                    _updating = true;
                    try
                    {
                        _store.Add(template);
                    }
                    finally
                    {
                        _updating = false;
                    }
                    last = template;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
                {
                    problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }

            if (last != null)
                Select(last);
            if (problems.Count > 0)
                AppDialog.Warning(this, "Import", problems.Count == 1 ? "A file wasn't imported" : "Some files weren't imported",
                    string.Join("\n", problems));
        }

        // Import and export start in the folder used last time (not in the templates folder itself)
        private static string? ExchangeFolder =>
            App.Settings.Current.Templates.LastExchangeFolder is { } folder && Directory.Exists(folder) ? folder : null;

        private static void RememberExchangeFolder(string file)
        {
            App.Settings.Current.Templates.LastExchangeFolder = Path.GetDirectoryName(file);
            App.Settings.RequestSave();
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (!TryCommit() || _selected == null)
                return;
            var invalid = Path.GetInvalidFileNameChars();
            var dialog = new SaveFileDialog
            {
                Title = "Export table template",
                Filter = "Table template (*.json)|*.json",
                FileName = new string(_selected.Name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()) + ".json",
                InitialDirectory = ExchangeFolder
            };
            if (dialog.ShowDialog(this) != true)
                return;
            RememberExchangeFolder(dialog.FileName);
            try
            {
                TemplateStore.Write(_selected, dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppDialog.Error(this, "Table templates", "Couldn't export the template", ex.Message);
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(_store.Folder);
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { _store.Folder } });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                AppDialog.Error(this, "Table templates", "Couldn't open the templates folder", ex.Message);
            }
        }

        #endregion

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (!TryCommit())
                e.Cancel = true;
        }
    }
}
