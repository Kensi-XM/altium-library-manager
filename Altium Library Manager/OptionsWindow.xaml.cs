using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Altium_Library_Manager.Services.Settings;
using Altium_Library_Manager.Services.Templates;
using Microsoft.Win32;

namespace Altium_Library_Manager
{
    /// <summary>
    /// Options dialog. Edits a copy of the settings (its DataContext); Apply and OK hand a copy of the edited settings to
    /// the owner, Cancel discards the changes since the last Apply.
    /// </summary>
    public partial class OptionsWindow : Window
    {
        private readonly UserSettings _working;
        private readonly Action<UserSettings> _apply;
        private readonly FrameworkElement[] _pages;

        public OptionsWindow(UserSettings working, Action<UserSettings> apply)
        {
            InitializeComponent();
            _working = working;
            _apply = apply;
            DataContext = working;

            ThemeCombo.ItemsSource = Enum.GetValues<AppTheme>();
            DensityCombo.ItemsSource = Enum.GetValues<TableDensity>();
            GridLinesCombo.ItemsSource = Enum.GetValues<GridLines>();
            DetailCombo.ItemsSource = Enum.GetValues<ModelDetail>();

            _pages = [GeneralPage, AppearancePage, TablesPage, EditingPage, PreviewPage, TemplatesPage];
            Categories.SelectedIndex = 0;
            UpdateWidthControls();
            FillStartOptions();
            UpdateFolderControls();
            UpdateRestoreBuiltIn();
        }

        #region Templates page

        private record StartOption(string Label, NewTableStart Start, string? Template = null)
        {
            // Screen readers read combo box items by their text
            public override string ToString() => Label;
        }

        /// <summary>The built-in choices, then the templates under a "Templates" heading (by name only).</summary>
        private void FillStartOptions()
        {
            var options = new List<StartOption>
            {
                new("The one used last time", NewTableStart.LastUsed),
                new("Minimal (Part Number, symbol, footprint)", NewTableStart.Minimal),
                new("Empty table (Part Number only)", NewTableStart.Empty)
            };
            var templates = App.Templates.Templates.Select(t => new StartOption(t.Name, NewTableStart.Template, t.Name)).ToList();

            StartCombo.Items.Clear();
            foreach (var option in options)
                StartCombo.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option });
            if (templates.Count > 0)
            {
                StartCombo.Items.Add(new Separator());
                StartCombo.Items.Add(new ComboBoxItem
                {
                    Content = "Templates", IsEnabled = false, FontWeight = FontWeights.SemiBold, Focusable = false
                });
                foreach (var option in templates)
                    StartCombo.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option, Padding = new Thickness(24, 6, 12, 6) });
            }

            var settings = _working.Templates;
            StartCombo.SelectedItem = StartCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i =>
                                          i.Tag is StartOption o && o.Start == settings.NewTableStart &&
                                          (o.Start != NewTableStart.Template || o.Template == settings.StartTemplate))
                                      ?? StartCombo.Items[0];
        }

        private void StartCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if ((StartCombo.SelectedItem as ComboBoxItem)?.Tag is not StartOption option)
                return;
            _working.Templates.NewTableStart = option.Start;
            _working.Templates.StartTemplate = option.Template;
        }

        private void UpdateFolderControls()
        {
            var folder = _working.Templates.Folder;
            FolderText.Text = string.IsNullOrWhiteSpace(folder) ? $"{TemplateStore.DefaultFolder} (default)" : folder;
            FolderText.ToolTip = FolderText.Text;
            DefaultFolderButton.IsEnabled = !string.IsNullOrWhiteSpace(folder);
        }

        private void ChangeFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Folder for table templates",
                InitialDirectory = Directory.Exists(_working.Templates.Folder) ? _working.Templates.Folder : TemplateStore.DefaultFolder
            };
            if (dialog.ShowDialog(this) != true)
                return;
            // The default folder is kept as "default", so it follows the user profile
            _working.Templates.Folder = string.Equals(Path.GetFullPath(dialog.FolderName).TrimEnd('\\'),
                TemplateStore.DefaultFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? null : dialog.FolderName;
            UpdateFolderControls();
        }

        private void DefaultFolder_Click(object sender, RoutedEventArgs e)
        {
            _working.Templates.Folder = null;
            UpdateFolderControls();
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = string.IsNullOrWhiteSpace(_working.Templates.Folder) ? TemplateStore.DefaultFolder : _working.Templates.Folder;
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder } });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
            {
                AppDialog.Error(this, "Options", "Couldn't open the templates folder", ex.Message);
            }
        }

        private const string BuiltInName = "Altium component";

        private void UpdateRestoreBuiltIn()
        {
            var present = App.Templates.Templates.Any(t => string.Equals(t.Name, BuiltInName, StringComparison.OrdinalIgnoreCase));
            RestoreBuiltInButton.IsEnabled = !present;
            RestoreBuiltInText.Text = present ? "It's in the templates." : "";
        }

        // Takes effect at once (like the template manager), not on OK
        private void RestoreBuiltIn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                App.Templates.Add(BuiltInTemplates.AltiumComponent());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppDialog.Error(this, "Options", "Couldn't save the template", ex.Message);
                return;
            }
            UpdateRestoreBuiltIn();
            FillStartOptions();
        }

        #endregion

        private void SizingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateWidthControls();

        // Only the controls of the chosen sizing mode are shown; the reset button is active only when there is
        // something to reset
        private void UpdateWidthControls()
        {
            if (FitContentPanel == null)
                return;
            var fit = _working.Tables.ColumnSizing == ColumnSizing.FitContent;
            FitContentPanel.Visibility = fit ? Visibility.Visible : Visibility.Collapsed;
            FixedPanel.Visibility = fit ? Visibility.Collapsed : Visibility.Visible;

            var tables = _working.Tables.ColumnWidths.Count(t => t.Value.Count > 0);
            var details = _working.Tables.DetailsFieldWidth != null;
            ResetWidthsButton.IsEnabled = tables > 0 || details;
            RememberedWidthsText.Text = (tables, details) switch
            {
                (0, false) => "Nothing remembered yet",
                (0, true) => "Details Field column",
                (_, false) => tables == 1 ? "1 table" : $"{tables} tables",
                _ => (tables == 1 ? "1 table" : $"{tables} tables") + " and the Details Field column"
            };
        }

        private void ResetWidths_Click(object sender, RoutedEventArgs e)
        {
            _working.Tables.ColumnWidths.Clear();
            _working.Tables.DetailsFieldWidth = null;
            UpdateWidthControls();
        }

        // What the user built up (widths, search columns) and the templates page stay; applied by OK or Apply, like any option
        private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
        {
            if (!AppDialog.Confirm(this, "Options", "Put the options back to their defaults?", "Restore defaults",
                    "Templates, remembered column widths and search columns stay. Nothing changes until you press OK or Apply."))
                return;
            var tables = _working.Tables;
            _working.General = new GeneralSettings();
            _working.Appearance = new AppearanceSettings();
            _working.Preview = new PreviewSettings();
            _working.Editing = new EditingSettings();
            _working.Tables = new TablesSettings
            {
                ColumnWidths = tables.ColumnWidths,
                SearchColumns = tables.SearchColumns,
                DetailsFieldWidth = tables.DetailsFieldWidth
            };
            // The settings don't announce changes: the controls read them again
            DataContext = null;
            DataContext = _working;
            UpdateWidthControls();
        }

        private void Categories_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            for (var i = 0; i < _pages.Length; i++)
                _pages[i].Visibility = i == Categories.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        }

        // A copy, so edits made after Apply don't leak into the live settings until applied again
        private void Apply_Click(object sender, RoutedEventArgs e) => _apply(SettingsStore.Clone(_working));

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            _apply(SettingsStore.Clone(_working));
            DialogResult = true;
        }
    }
}
