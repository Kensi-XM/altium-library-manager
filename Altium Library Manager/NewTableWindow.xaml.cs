using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Altium_Library_Manager.Services;
using Altium_Library_Manager.Services.Settings;
using Altium_Library_Manager.Services.Templates;

namespace Altium_Library_Manager
{
    /// <summary>
    /// Creates a table in the open database, empty or from a template. The template's fields are copied into the
    /// editor, so they can be adjusted for this table without changing the template.
    /// </summary>
    public partial class NewTableWindow : Window
    {
        private readonly AccessDatabase _database;
        private readonly HashSet<string> _existingTables;
        // Built-in starting points, listed above the templates
        private readonly TableTemplate _minimal = BuiltInTemplates.Minimal();
        private readonly TableTemplate _empty = BuiltInTemplates.Empty();

        // The source the editor's fields came from; switching sources asks first if they were edited
        private TableTemplate? _source;
        private bool _edited;
        private bool _loading;

        public NewTableWindow(AccessDatabase database, IEnumerable<string> existingTables)
        {
            InitializeComponent();
            _database = database;
            _existingTables = new HashSet<string>(existingTables, StringComparer.OrdinalIgnoreCase);
            DatabaseText.Text = $"Added to {Path.GetFileName(database.FilePath)}";

            var initial = InitialSource();
            FillSources(initial);
            LoadSource(initial);
            Loaded += (_, _) => NameBox.Focus();
        }

        /// <summary>The name of the table that was created (when the dialog returns true).</summary>
        public string? CreatedTable { get; private set; }

        /// <summary>Lists the built-in starting points and the templates, selecting <paramref name="select"/> without loading its fields.</summary>
        private void FillSources(TableTemplate select)
        {
            _loading = true;
            Sources.ItemsSource = new[] { _minimal, _empty }.Concat(App.Templates.Templates).ToList();
            Sources.SelectedItem = select;
            _loading = false;
        }

        private bool IsBuiltIn(TableTemplate source) => source == _minimal || source == _empty;

        /// <summary>What Options → Templates says to start with (the source of the last table, by default).</summary>
        private TableTemplate InitialSource()
        {
            var settings = App.Settings.Current;
            var name = settings.Templates.NewTableStart switch
            {
                NewTableStart.Minimal => _minimal.Name,
                NewTableStart.Empty => _empty.Name,
                NewTableStart.Template => settings.Templates.StartTemplate,
                _ => settings.Session.LastNewTableSource
            };
            return new[] { _minimal, _empty }.Concat(App.Templates.Templates)
                .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? _minimal;
        }

        private void Sources_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || Sources.SelectedItem is not TableTemplate source || source == _source)
                return;

            if (_edited && !AppDialog.Confirm(this, "New table", $"Use the fields of \"{source.Name}\"?", "Replace fields",
                    "The changes you made to the fields will be lost."))
            {
                _loading = true;
                Sources.SelectedItem = _source;
                _loading = false;
                return;
            }
            LoadSource(source);
        }

        private void LoadSource(TableTemplate source)
        {
            _source = source;
            _loading = true;
            Editor.Fields = new ObservableCollection<TemplateField>(source.Fields.Select(f => f.Clone()));
            _loading = false;
            _edited = false;

            var description = string.IsNullOrWhiteSpace(source.Description) ? "" : source.Description + " ";
            SourceText.Text = IsBuiltIn(source)
                ? description
                : description + "Changes here are for this table only; the template stays as it is.";
            Validate();
        }

        private void Editor_Changed(object? sender, EventArgs e)
        {
            if (_loading)
                return;
            _edited = true;
            Validate();
        }

        private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => Validate();

        /// <summary>Shows the first problem and enables Create only when there is none.</summary>
        private bool Validate()
        {
            if (CreateButton == null)
                return false;
            var name = NameBox.Text.Trim();
            var fields = Editor.Fields ?? [];

            // An empty name is the starting state, not a mistake worth a red message
            var error = name.Length == 0 ? null
                : TemplateRules.CheckName(name, "Table")
                  ?? (_existingTables.Contains(name) ? $"The database already has a table named \"{name}\"." : null);
            error ??= TemplateRules.CheckFields(fields).FirstOrDefault();

            ErrorText.Text = error;
            ErrorText.ToolTip = error;
            var ok = error == null && name.Length > 0;
            CreateButton.IsEnabled = ok;
            return ok;
        }

        private async void Create_Click(object sender, RoutedEventArgs e)
        {
            if (!Validate())
                return;
            var name = NameBox.Text.Trim();
            var fields = Editor.Fields!.ToList();

            IsEnabled = false;
            _creating = true;
            try
            {
                await Task.Run(() => _database.CreateTable(name, fields));
            }
            catch (Exception ex)
            {
                // Any failure (database locked, read-only, provider missing…) is reported, never left to end the app
                _creating = false;
                IsEnabled = true;
                AppDialog.Error(this, "New table", "Couldn't create the table", ex.Message);
                return;
            }
            _creating = false;

            CreatedTable = name;
            // For "Start with: the one used last time" (Options → Templates)
            App.Settings.Current.Session.LastNewTableSource = _source?.Name;
            App.Settings.RequestSave();
            DialogResult = true;
        }

        // While the table is being created the window stays open (Alt+F4 included), so the main window learns
        // about the new table
        private bool _creating;

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => e.Cancel = _creating;

        private void SaveAsTemplate_Click(object sender, RoutedEventArgs e)
        {
            var fields = Editor.Fields ?? [];
            if (TemplateRules.CheckFields(fields).FirstOrDefault() is { } error)
            {
                AppDialog.Warning(this, "New table", "These fields can't be saved as a template", error);
                return;
            }

            var template = new TableTemplate
            {
                Name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : "New template",
                Description = _source != null && !IsBuiltIn(_source) ? $"Based on \"{_source.Name}\"." : "",
                Fields = new ObservableCollection<TemplateField>(fields.Select(f => f.Clone()))
            };
            try
            {
                App.Templates.Add(template);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppDialog.Error(this, "New table", "Couldn't save the template", ex.Message);
                return;
            }

            // The editor keeps its fields: they are now exactly the new template's
            _source = template;
            FillSources(template);
            _edited = false;
            SourceText.Text = $"Saved as the template \"{template.Name}\".";
        }

        private void ManageTemplates_Click(object sender, RoutedEventArgs e)
        {
            new TemplatesWindow(_source != null && !IsBuiltIn(_source) ? _source : null) { Owner = this }.ShowDialog();

            // The selected template may have been changed, renamed or deleted meanwhile; the editor keeps its fields
            // either way (picking the template again loads its new fields)
            _source = IsBuiltIn(_source!) || App.Templates.Templates.Contains(_source!) ? _source! : _minimal;
            FillSources(_source);
        }
    }
}
