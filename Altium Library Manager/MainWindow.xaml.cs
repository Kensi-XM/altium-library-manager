using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Altium_Library_Manager.Services;
using Altium_Library_Manager.Services.Settings;
using Altium_Library_Manager.Services.Templates;
using Microsoft.Win32;

namespace Altium_Library_Manager
{
    /// <summary>
    /// Layout: database and its tables on the left (1), open tables as tabs in the middle (2),
    /// fields of the selected row on the right (4), symbol/footprint/3D preview at the bottom (3).
    /// Panels 1, 3 and 4 can be collapsed; their sizes, the last database and its open tabs are remembered.
    /// </summary>
    public partial class MainWindow : Window
    {
        private const double MinSlotHeight = 150;
        private const double MinSidePanelWidth = 160;

        /// <summary>
        /// A layout preset: which panel occupies the center (2), right (4) and bottom (3) slots, and the default
        /// size of the right and bottom slots (the user's own sizes are then remembered per preset).
        /// </summary>
        private record LayoutPreset(string Name, LayoutSlot Tables, LayoutSlot Details, LayoutSlot Preview,
            double RightWidth, double BottomHeight);

        // In menu / Ctrl+1…4 order; the active preset is saved by index (see SettingsStore when reordering)
        private static readonly LayoutPreset[] Layouts =
        [
            new("Side preview", Tables: LayoutSlot.Center, Details: LayoutSlot.Bottom, Preview: LayoutSlot.Right, 560, 240),
            new("Row", Tables: LayoutSlot.Bottom, Details: LayoutSlot.Center, Preview: LayoutSlot.Right, 560, 280),
            new("Table", Tables: LayoutSlot.Center, Details: LayoutSlot.Right, Preview: LayoutSlot.Bottom, 320, 320),
            new("Preview", Tables: LayoutSlot.Bottom, Details: LayoutSlot.Right, Preview: LayoutSlot.Center, 320, 280)
        ];

        private readonly AltiumRenderService _renderService = new();
        private readonly SettingsStore _store = App.Settings;
        private UserSettings _settings => _store.Current;
        private readonly ObservableCollection<TableTab> _tabs = [];

        private AccessDatabase? _database;
        private int _layout = -1;

        public MainWindow()
        {
            InitializeComponent();
            HorizontalWheel.Attach(this);
            TableTab.GridRowStyle = (Style)FindResource("GridRow");
            TableTab.GridCellStyle = (Style)FindResource("GridCell");
            TableTab.GridColumnHeaderStyle = (Style)FindResource("GridColumnHeader");
            TableTab.GridEditingTextStyle = (Style)FindResource("GridEditingText");
            TableTab.SettingsChanged += _store.RequestSave;
            // handledEventsToo: the column header marks its gripper's drag as handled
            DetailsGrid.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(DetailsGrid_ColumnResized), handledEventsToo: true);
            Preview.RenderService = _renderService;
            TableTabs.ItemsSource = _tabs;

            foreach (var frame in new[] { TablesFrame, DetailsFrame, PreviewFrame })
                frame.CollapseToggled += Frame_CollapseToggled;

            // Drop sizes of presets that no longer exist (renamed or removed in an update)
            foreach (var name in _settings.Layout.Presets.Keys.Except(Layouts.Select(l => l.Name)).ToList())
                _settings.Layout.Presets.Remove(name);

            RestoreWindowPlacement();
            ApplySettings();
            SetDatabasePanelExpanded(!_settings.Layout.DatabasePanelCollapsed);
            ApplyLayout(Math.Clamp(_settings.Layout.ActivePreset, 0, Layouts.Length - 1));
            UpdateTabsHint();
            UpdateTableCommands();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Opened maximized: the window has its maximized bounds only now, to measure what is off the screen
            UpdateMaximizedState();
            if (_settings.General.RestoreSession && _settings.Session.LastDatabase != null && File.Exists(_settings.Session.LastDatabase))
                await OpenDatabaseAsync(_settings.Session.LastDatabase, restoreTabs: _settings.General.RestoreTabs);
        }

        #region Details Field column

        // The Field column fits the field names, but takes at most a share of the panel (Options → Tables), so a
        // narrow panel keeps room for the values; a width the user dragged is used as is (and remembered)

        private void DetailsGrid_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyDetailsFieldWidth();

        // Dragged in this session; kept even when widths aren't remembered across sessions
        private double? _detailsFieldWidth;

        private void ApplyDetailsFieldWidth()
        {
            var tables = _settings.Tables;
            if (((tables.RememberColumnWidths ? tables.DetailsFieldWidth : null) ?? _detailsFieldWidth) is double width)
            {
                DetailsFieldColumn.MaxWidth = double.PositiveInfinity;
                DetailsFieldColumn.Width = new DataGridLength(width);
                return;
            }
            var share = Math.Clamp(tables.DetailsFieldMaxShare, 0.2, 0.7);
            DetailsFieldColumn.MaxWidth = Math.Max(DetailsFieldColumn.MinWidth, DetailsGrid.ActualWidth * share);
            if (!DetailsFieldColumn.Width.IsAuto)
                DetailsFieldColumn.Width = DataGridLength.Auto;
        }

        private void DetailsGrid_ColumnResized(object sender, DragCompletedEventArgs e)
        {
            if (e.OriginalSource is not Thumb { TemplatedParent: DataGridColumnHeader { Column: { } column } } ||
                column != DetailsFieldColumn && column.DisplayIndex != DetailsFieldColumn.DisplayIndex + 1)
                return;
            _detailsFieldWidth = Math.Round(DetailsFieldColumn.ActualWidth);
            if (!_settings.Tables.RememberColumnWidths)
                return;
            _settings.Tables.DetailsFieldWidth = _detailsFieldWidth;
            _store.RequestSave();
        }

        #endregion

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            // A row being edited is saved first; if it can't be and the user keeps editing, the app stays open
            if (!ConfirmClose(_tabs))
            {
                e.Cancel = true;
                return;
            }

            if (DatabasePanel.Visibility == Visibility.Visible)
                _settings.Layout.DatabasePanelWidth = DatabaseColumn.ActualWidth;
            RememberSlotSizes();

            _settings.Session.OpenTables = _tabs.Select(t => t.Name).ToList();
            _settings.Session.PinnedTables = _tabs.Where(t => t.IsPinned).Select(t => t.Name).ToList();
            _settings.Session.ActiveTable = (TableTabs.SelectedItem as TableTab)?.Name;
            _settings.Session.PreviewTable = _tabs.FirstOrDefault(t => t.IsPreview)?.Name;
            SaveWindowPlacement();
            _store.Save();
            HelpWindow.CloseIfOpen();
        }

        #region Window placement

        // Below this the panels no longer fit side by side; it still fits a Full HD screen at 150% scaling (1280x720)
        private const double MinWindowWidth = 960, MinWindowHeight = 600;

        /// <summary>
        /// Restores the saved size, position and maximized state. Without saved placement (first start), the window
        /// takes most of the primary screen, centered. Either way it is fitted into the work area of its monitor once
        /// it has a handle (<see cref="ScreenFit"/>).
        /// </summary>
        private void RestoreWindowPlacement()
        {
            (MinWidth, MinHeight) = (MinWindowWidth, MinWindowHeight);

            var window = _settings.Window;
            if (window.Width is not double width || window.Height is not double height ||
                window.Left is not double left || window.Top is not double top)
            {
                var work = SystemParameters.WorkArea;
                Width = Math.Max(Math.Min(1500, work.Width * 0.9), MinWindowWidth);
                Height = Math.Max(Math.Min(950, work.Height * 0.9), MinWindowHeight);
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                return;
            }

            // Ignore a saved position that is no longer on any screen (e.g. a disconnected monitor)
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!screen.IntersectsWith(new Rect(left, top, width, height)))
                return;

            WindowStartupLocation = WindowStartupLocation.Manual;
            (Left, Top, Width, Height) = (left, top, width, height);
            if (window.Maximized)
                WindowState = WindowState.Maximized;
        }

        private void SaveWindowPlacement()
        {
            // RestoreBounds is the normal-state size even while maximized
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            // A window closed before it was shown has no size yet, and JSON can't hold NaN or infinity
            if (!double.IsFinite(bounds.Left + bounds.Top + bounds.Width + bounds.Height))
                return;
            var window = _settings.Window;
            (window.Left, window.Top, window.Width, window.Height) = (bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            window.Maximized = WindowState == WindowState.Maximized;
        }

        #endregion

        #region Title bar

        private const int WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, WM_NCMOUSELEAVE = 0x02A2;
        private const int HTMAXBUTTON = 9;

        private void Window_SourceInitialized(object? sender, EventArgs e)
        {
            // Deferred: the last hook added runs first, and this one has to see WM_NCHITTEST before WindowChrome's
            Dispatcher.BeginInvoke(() => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(WndProc));
            ScreenFit.FitToWorkArea(this);
            UpdateMaximizedState();
        }

        // A maximized window moved to a monitor with another scale (Win+Shift+arrow): its off-screen part is measured again
        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            if (WindowState == WindowState.Maximized)
                Dispatcher.BeginInvoke(UpdateMaximizedState, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            UpdateMaximizedState();
            // The normal size may have been saved on a larger screen than the one it is restored on
            if (WindowState == WindowState.Normal)
                ScreenFit.FitToWorkArea(this);
        }

        private void UpdateMaximizedState()
        {
            var maximized = WindowState == WindowState.Maximized;
            // The part of a maximized window that is off the screen (measured: the system value misses the padded border)
            WindowRoot.Padding = maximized
                ? ScreenFit.MaximizedOverhang(this) ?? SystemParameters.WindowResizeBorderThickness
                : new Thickness(0);
            MaximizeButton.Content = maximized ? "" : "";
            MaximizeButton.ToolTip = maximized ? "Restore down" : "Maximize";
            UpdateCaptionHeight();
        }

        private void TitleBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCaptionHeight();

        // The drag area must match the title bar, which grows with the interface scale
        private void UpdateCaptionHeight()
        {
            var chrome = WindowChrome.GetWindowChrome(this);
            var height = TitleBar.ActualHeight * Math.Clamp(_settings.Appearance.UiScale, 0.5, 2.0) + WindowRoot.Padding.Top;
            if (chrome != null && Math.Abs(chrome.CaptionHeight - height) > 0.5)
                chrome.CaptionHeight = height;
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        private void MaximizeRestore_Click(object sender, RoutedEventArgs e) => ToggleMaximized();

        private void ToggleMaximized()
        {
            if (WindowState == WindowState.Maximized)
                SystemCommands.RestoreWindow(this);
            else
                SystemCommands.MaximizeWindow(this);
        }

        /// <summary>
        /// Reports the maximize button to Windows as the caption's maximize button, so Windows 11 shows the snap
        /// layouts flyout over it. Windows then owns its mouse input, so hover, press and click are handled here.
        /// </summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_NCHITTEST:
                    var overMaximize = IsOverMaximizeButton(lParam);
                    if (!overMaximize)
                    {
                        MaximizeButton.Tag = null;
                        return IntPtr.Zero;
                    }
                    if (MaximizeButton.Tag == null)
                        MaximizeButton.Tag = "Hover";
                    handled = true;
                    return HTMAXBUTTON;
                case WM_NCLBUTTONDOWN when wParam == HTMAXBUTTON:
                    MaximizeButton.Tag = "Pressed";
                    handled = true;
                    break;
                case WM_NCLBUTTONUP when wParam == HTMAXBUTTON:
                    var pressed = Equals(MaximizeButton.Tag, "Pressed");
                    MaximizeButton.Tag = null;
                    handled = true;
                    if (pressed)
                        ToggleMaximized();
                    break;
                case WM_NCMOUSELEAVE:
                    MaximizeButton.Tag = null;
                    break;
            }
            return IntPtr.Zero;
        }

        private bool IsOverMaximizeButton(IntPtr lParam)
        {
            if (!MaximizeButton.IsVisible)
                return false;
            // Screen coordinates in physical pixels; PointToScreen includes DPI and the interface scale
            var x = (short)(lParam.ToInt64() & 0xFFFF);
            var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            var topLeft = MaximizeButton.PointToScreen(new Point(0, 0));
            var bottomRight = MaximizeButton.PointToScreen(new Point(MaximizeButton.ActualWidth, MaximizeButton.ActualHeight));
            return x >= topLeft.X && x < bottomRight.X && y >= topLeft.Y && y < bottomRight.Y;
        }

        #endregion

        private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (HandleTabKeys(e))
                return;

            var activeTab = TableTabs.SelectedItem as TableTab;
            if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None && activeTab != null)
            {
                e.Handled = true;
                ReloadTab(activeTab);
                return;
            }
            if (HandleHistoryKeys(e, activeTab))
                return;

            // Ctrl+O opens a database, Ctrl+N adds a table, Ctrl+F searches, Ctrl+E edits the table, Ctrl+W closes the tab, Ctrl+1…4 switch the layout preset (as shown in the menus)
            if (Keyboard.Modifiers != ModifierKeys.Control)
                return;
            if (e.Key == Key.E && activeTab != null)
            {
                e.Handled = true;
                activeTab.IsEditing = !activeTab.IsEditing;
                return;
            }
            if (e.Key == Key.O)
            {
                e.Handled = true;
                await OpenDatabaseInteractiveAsync();
                return;
            }
            if (e.Key == Key.OemComma)
            {
                e.Handled = true;
                ShowOptions();
                return;
            }
            if (e.Key == Key.N)
            {
                e.Handled = true;
                ShowNewTable();
                return;
            }
            if (e.Key == Key.F)
            {
                // Where the keyboard is: in the database panel it filters the tables, elsewhere it searches the rows
                // of the open table
                e.Handled = true;
                if (activeTab != null && !DatabasePanel.IsKeyboardFocusWithin)
                    activeTab.View.FocusSearch();
                else
                    FocusTableFilter();
                return;
            }
            if (e.Key is Key.W or Key.F4)
            {
                e.Handled = true;
                CloseActiveTab();
                return;
            }
            var index = e.Key - Key.D1;
            if (index < 0 || index >= Layouts.Length)
                index = e.Key - Key.NumPad1;
            if (index >= 0 && index < Layouts.Length)
            {
                ApplyLayout(index);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Ctrl+S saves the active table (Ctrl+Shift+S all of them); Ctrl+Z / Ctrl+Y (or Ctrl+Shift+Z) undo and redo
        /// its changes. In a text box (the search, a cell being typed in) Ctrl+Z / Ctrl+Y are the text box's own.
        /// </summary>
        private bool HandleHistoryKeys(KeyEventArgs e, TableTab? tab)
        {
            var modifiers = Keyboard.Modifiers;
            if (!modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Alt))
                return false;
            var shift = modifiers.HasFlag(ModifierKeys.Shift);
            var inText = Keyboard.FocusedElement is TextBox;
            switch (e.Key)
            {
                case Key.S when shift:
                    SaveTables(_tabs);
                    break;
                case Key.S when tab != null:
                    SaveTables([tab]);
                    break;
                case Key.Z when !inText && tab != null:
                    if (shift)
                        tab.Redo();
                    else
                        tab.Undo();
                    break;
                case Key.Y when !inText && !shift && tab != null:
                    tab.Redo();
                    break;
                default:
                    return false;
            }
            e.Handled = true;
            return true;
        }

        /// <summary>Saves these tables' changes, stopping at the first one that can't be saved (its tab is shown).</summary>
        private void SaveTables(IEnumerable<TableTab> tabs)
        {
            var saved = 0;
            foreach (var tab in tabs.Where(t => t.IsDirty).ToList())
            {
                if (!tab.Save(this))
                {
                    TableTabs.SelectedItem = tab;
                    return;
                }
                saved++;
            }
            if (saved > 1)
                StatusText.Text = $"Saved {saved} tables";
        }

        private TableTab? ActiveTab => TableTabs.SelectedItem as TableTab;

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveTab is { } tab)
                SaveTables([tab]);
        }

        private void SaveAll_Click(object sender, RoutedEventArgs e) => SaveTables(_tabs);

        private void Undo_Click(object sender, RoutedEventArgs e) => ActiveTab?.Undo();

        private void Redo_Click(object sender, RoutedEventArgs e) => ActiveTab?.Redo();

        private void EditRows_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveTab is { } tab)
                tab.IsEditing = !tab.IsEditing;
        }

        private void AddRowMenu_Click(object sender, RoutedEventArgs e) => ActiveTab?.AddRow(duplicate: false);

        private void DuplicateRowMenu_Click(object sender, RoutedEventArgs e) => ActiveTab?.AddRow(duplicate: true);

        private void AddElementsMenu_Click(object sender, RoutedEventArgs e) => ActiveTab?.RequestAddElements();

        private void DeleteRowsMenu_Click(object sender, RoutedEventArgs e) => ActiveTab?.DeleteSelectedRows();

        private void ReloadMenu_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveTab is { } tab)
                ReloadTab(tab);
        }

        private void EditMenu_SubmenuOpened(object sender, RoutedEventArgs e) => UpdateTableCommands();

        /// <summary>Enables save / undo / redo (title bar, menus) for the active table, with what they would do in
        /// their tooltips.</summary>
        private void UpdateTableCommands()
        {
            var tab = ActiveTab;
            SaveButton.IsEnabled = SaveMenu.IsEnabled = tab is { IsDirty: true };
            SaveAllMenu.IsEnabled = _tabs.Any(t => t.IsDirty);
            UndoButton.IsEnabled = UndoMenu.IsEnabled = tab is { CanUndo: true };
            RedoButton.IsEnabled = RedoMenu.IsEnabled = tab is { CanRedo: true };
            SaveButton.ToolTip = tab is { IsDirty: true } ? $"Save the changes to {tab.Name} (Ctrl+S)" : "Save table (Ctrl+S): no unsaved changes";
            UndoButton.ToolTip = tab?.UndoDescription is { } undo ? $"Undo: {undo} (Ctrl+Z)" : "Undo (Ctrl+Z)";
            RedoButton.ToolTip = tab?.RedoDescription is { } redo ? $"Redo: {redo} (Ctrl+Y)" : "Redo (Ctrl+Y)";
            // A plain text header: the description may contain '_', which a string header would take as an access key
            UndoMenu.Header = new TextBlock { Text = tab?.UndoDescription is { } u ? $"Undo {u}" : "Undo" };
            RedoMenu.Header = new TextBlock { Text = tab?.RedoDescription is { } r ? $"Redo {r}" : "Redo" };

            EditRowsMenu.IsEnabled = tab is { CanEdit: true };
            EditRowsMenu.IsChecked = tab is { IsEditing: true };
            AddRowMenu.IsEnabled = AddElementsMenu.IsEnabled = DuplicateRowMenu.IsEnabled = DeleteRowsMenu.IsEnabled = tab is { IsEditing: true };
            ReloadMenu.IsEnabled = tab != null;
        }

        /// <summary>
        /// Switching tables from the keyboard: Alt+1…8 picks a tab by its place, Alt+9 the last one (as in browsers);
        /// Ctrl+Tab / Ctrl+Shift+Tab and Ctrl+PageDown / Ctrl+PageUp go to the next / previous tab, wrapping around.
        /// </summary>
        private bool HandleTabKeys(KeyEventArgs e)
        {
            if (_tabs.Count == 0)
                return false;
            var current = TableTabs.SelectedItem is TableTab tab ? _tabs.IndexOf(tab) : -1;
            int? target = null;

            // With Alt held, WPF reports the key as System and the real key in SystemKey
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (Keyboard.Modifiers == ModifierKeys.Alt)
            {
                var digit = key is >= Key.D1 and <= Key.D9 ? key - Key.D1 + 1
                    : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad1 + 1
                    : 0;
                if (digit == 9)
                    target = _tabs.Count - 1;
                else if (digit > 0 && digit <= _tabs.Count)
                    target = digit - 1;
                else if (digit > 0)
                {
                    // Alt+5 with three tabs: nothing to switch to, but the key is still ours (no menu access key)
                    e.Handled = true;
                    return true;
                }
            }
            else if (key == Key.Tab && Keyboard.Modifiers is ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift))
                target = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? current - 1 : current + 1;
            else if (key is Key.PageDown or Key.PageUp && Keyboard.Modifiers == ModifierKeys.Control)
                target = key == Key.PageUp ? current - 1 : current + 1;

            if (target is not { } index)
                return false;
            e.Handled = true;
            index = (index % _tabs.Count + _tabs.Count) % _tabs.Count;
            if (index != current)
                SelectTab(_tabs[index]);
            return true;
        }

        /// <summary>Activates a tab; when the keyboard was in the tables, it moves into the newly shown table.</summary>
        private void SelectTab(TableTab tab)
        {
            var focusInTables = TableTabs.IsKeyboardFocusWithin;
            TableTabs.SelectedItem = tab;
            if (focusInTables)
                Dispatcher.BeginInvoke(() => Keyboard.Focus(tab.Grid), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        #region Layout presets

        private LayoutState CurrentLayoutState => LayoutStateOf(Layouts[_layout]);

        private LayoutState LayoutStateOf(LayoutPreset preset)
        {
            if (!_settings.Layout.Presets.TryGetValue(preset.Name, out var state))
                _settings.Layout.Presets[preset.Name] = state = new LayoutState { RightWidth = preset.RightWidth, BottomHeight = preset.BottomHeight };
            return state;
        }

        private void LayoutMenu_Click(object sender, RoutedEventArgs e) =>
            ApplyLayout(int.Parse((string)((FrameworkElement)sender).Tag));

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

        #region Options

        private void Options_Click(object sender, RoutedEventArgs e) => ShowOptions();

        private void Help_Click(object sender, RoutedEventArgs e) => HelpWindow.Open(((FrameworkElement)sender).Tag as string);

        private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

        /// <summary>The dialog edits a copy; Apply / OK copy the edited sections back here.</summary>
        private void ShowOptions()
        {
            var dialog = new OptionsWindow(SettingsStore.Clone(_settings), ApplyFromOptions) { Owner = this };
            dialog.ShowDialog();
        }

        private void ApplyFromOptions(UserSettings edited)
        {
            _settings.General = edited.General;
            _settings.Appearance = edited.Appearance;
            // "Reset remembered widths" also drops the Field width dragged in this session
            if (edited.Tables.DetailsFieldWidth == null && _settings.Tables.DetailsFieldWidth != null)
                _detailsFieldWidth = null;
            _settings.Tables = edited.Tables;
            _settings.Preview = edited.Preview;
            _settings.Editing = edited.Editing;
            var oldFolder = _settings.Templates.Folder;
            _settings.Templates = edited.Templates;
            if (!string.Equals(oldFolder ?? "", edited.Templates.Folder ?? "", StringComparison.OrdinalIgnoreCase))
                SwitchTemplatesFolder(oldFolder, edited.Templates.Folder);
            ApplySettings();
            _store.RequestSave();
        }

        /// <summary>
        /// Templates are read from the new folder from now on. When the new folder has no templates yet, offers to
        /// copy the old folder's there (files that exist are never overwritten; the old folder is left as it is).
        /// </summary>
        private void SwitchTemplatesFolder(string? oldFolder, string? newFolder)
        {
            var from = new TemplateStore(oldFolder).Folder;
            var to = new TemplateStore(newFolder).Folder;
            try
            {
                var files = Directory.Exists(from) ? Directory.GetFiles(from, "*.json") : [];
                var targetEmpty = !Directory.Exists(to) || Directory.GetFiles(to, "*.json").Length == 0;
                if (files.Length > 0 && targetEmpty && AppDialog.Show(OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this,
                        DialogKind.Question, "Templates folder",
                        $"Copy your {(files.Length == 1 ? "template" : $"{files.Length} templates")} to the new folder?",
                        $"From {from}\nto {to}\n\nThe old folder keeps its copies.", "Copy", "Don't copy") == 0)
                {
                    Directory.CreateDirectory(to);
                    foreach (var file in files)
                    {
                        var target = Path.Combine(to, Path.GetFileName(file));
                        if (!File.Exists(target))
                            File.Copy(file, target);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                AppDialog.Error(this, "Templates folder", "Couldn't copy the templates", ex.Message);
            }
            App.ReloadTemplates();
        }

        /// <summary>Applies the user-tunable settings (Options dialog) to the window.</summary>
        private void ApplySettings()
        {
            if (ThemeManager.Theme != _settings.Appearance.Theme)
                ThemeManager.Apply(_settings.Appearance.Theme);
            UpdateThemeMenu();

            // Scaling the window content, independently of the Windows display scaling
            var scale = Math.Clamp(_settings.Appearance.UiScale, 0.5, 2.0);
            RootPanel.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
            UpdateCaptionHeight();

            TableTab.RowHeight = _settings.Tables.Density switch
            {
                TableDensity.Normal => 24,
                TableDensity.Comfortable => 28,
                _ => 20
            };
            TableTab.Columns = _settings.Tables;
            TableTab.Editing = _settings.Editing;
            ApplyDetailsFieldWidth();
            TableTab.GridLines = _settings.Tables.GridLines switch
            {
                GridLines.None => DataGridGridLinesVisibility.None,
                GridLines.Horizontal => DataGridGridLinesVisibility.Horizontal,
                _ => DataGridGridLinesVisibility.All
            };
            TableTab.AlternatingRows = _settings.Tables.AlternatingRows;
            TableTab.FreezeFirstColumn = _settings.Tables.FreezeFirstColumn;
            foreach (var tab in _tabs)
                tab.ApplyGridSettings();

            Preview.ApplyOptions(new PreviewPanel.PreviewOptions(_settings.Appearance.DarkFootprintBackground,
                _settings.Preview.ShowBoard, _settings.Preview.ShowPads, _settings.Preview.ModelDetail,
                _settings.Preview.Show3D));
        }

        #endregion

        private void ThemeMenu_Click(object sender, RoutedEventArgs e)
        {
            var theme = Enum.Parse<AppTheme>((string)((FrameworkElement)sender).Tag);
            _settings.Appearance.Theme = theme;
            ThemeManager.Apply(theme);
            UpdateThemeMenu();
            _store.RequestSave();
        }

        private void UpdateThemeMenu()
        {
            var theme = _settings.Appearance.Theme;
            ThemeSystemMenu.IsChecked = theme == AppTheme.System;
            ThemeLightMenu.IsChecked = theme == AppTheme.Light;
            ThemeDarkMenu.IsChecked = theme == AppTheme.Dark;
        }

        /// <summary>
        /// Moves the panels into the preset's slots. Only Grid.Row/Column change, so nothing is rebuilt: tables keep
        /// their selection and scroll position and the 3D view isn't recreated.
        /// </summary>
        private void ApplyLayout(int index)
        {
            // Menu items toggle their own check mark on click; keep exactly the active preset checked
            var menuItems = new[] { LayoutMenu0, LayoutMenu1, LayoutMenu2, LayoutMenu3 };
            for (var i = 0; i < menuItems.Length; i++)
                menuItems[i].IsChecked = i == index;

            if (index == _layout)
                return;

            if (_layout >= 0)
                RememberSlotSizes();
            _layout = index;
            _settings.Layout.ActivePreset = index;
            _store.RequestSave();

            var preset = Layouts[index];
            PlaceInSlot(TablesFrame, preset.Tables);
            PlaceInSlot(DetailsFrame, preset.Details);
            PlaceInSlot(PreviewFrame, preset.Preview);
            ApplySlotSizes();
        }

        private static void PlaceInSlot(PanelFrame frame, LayoutSlot slot)
        {
            var (row, column, span) = slot switch
            {
                LayoutSlot.Right => (0, 2, 1),
                LayoutSlot.Bottom => (2, 0, 3),
                _ => (0, 0, 1)
            };
            Grid.SetRow(frame, row);
            Grid.SetColumn(frame, column);
            Grid.SetColumnSpan(frame, span);
            frame.Slot = slot;
        }

        private PanelFrame FrameIn(LayoutSlot slot) =>
            new[] { TablesFrame, DetailsFrame, PreviewFrame }.First(f => f.Slot == slot);

        /// <summary>Sizes the right and bottom slots from the current preset's state (collapsed slots shrink to fit).</summary>
        private void ApplySlotSizes()
        {
            var state = CurrentLayoutState;

            // A collapsed panel keeps the gap to its neighbour (the splitter), but the gap can't be dragged
            FrameIn(LayoutSlot.Right).IsCollapsed = state.RightCollapsed;
            RightSplitter.IsHitTestVisible = !state.RightCollapsed;
            RightColumn.MinWidth = state.RightCollapsed ? 0 : MinSidePanelWidth;
            RightColumn.Width = state.RightCollapsed
                ? GridLength.Auto
                : new GridLength(Math.Max(state.RightWidth, MinSidePanelWidth));

            FrameIn(LayoutSlot.Bottom).IsCollapsed = state.BottomCollapsed;
            BottomSplitter.IsHitTestVisible = !state.BottomCollapsed;
            BottomRow.MinHeight = state.BottomCollapsed ? 0 : MinSlotHeight;
            BottomRow.Height = state.BottomCollapsed
                ? GridLength.Auto
                : new GridLength(Math.Max(state.BottomHeight, MinSlotHeight));

            FrameIn(LayoutSlot.Center).IsCollapsed = false;

            // After layout, when the actual sizes are known
            Dispatcher.BeginInvoke(FitPanelsToWindow, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Saved panel sizes can be too large for the window (e.g. sized in a maximized window, then restored smaller).
        /// Then a grid gives every column its size anyway and the content runs off the window's edge, so the side
        /// panels are shrunk until the center keeps its minimum size.
        /// </summary>
        private void FitPanelsToWindow()
        {
            const double splitter = 5, minCenterWidth = 200, minCenterHeight = 120, minWorkWidth = 300;

            var state = CurrentLayoutState;
            if (!state.RightCollapsed)
                Shrink(RightColumn, WorkArea.ActualWidth - minCenterWidth - splitter, MinSidePanelWidth);
            if (!state.BottomCollapsed)
                ShrinkRow(BottomRow, WorkArea.ActualHeight - minCenterHeight - splitter, MinSlotHeight);
            if (DatabasePanel.Visibility == Visibility.Visible && WorkArea.Parent is FrameworkElement root)
                Shrink(DatabaseColumn, root.ActualWidth - minWorkWidth - splitter, MinSidePanelWidth);

            static void Shrink(ColumnDefinition column, double max, double min)
            {
                if (max >= min && column.ActualWidth > max)
                    column.Width = new GridLength(max);
            }

            static void ShrinkRow(RowDefinition row, double max, double min)
            {
                if (max >= min && row.ActualHeight > max)
                    row.Height = new GridLength(max);
            }
        }

        private void WorkArea_SizeChanged(object sender, SizeChangedEventArgs e) => FitPanelsToWindow();

        private void RememberSlotSizes()
        {
            var state = CurrentLayoutState;
            if (!state.RightCollapsed && RightColumn.ActualWidth > 0)
                state.RightWidth = RightColumn.ActualWidth;
            if (!state.BottomCollapsed && BottomRow.ActualHeight > 0)
                state.BottomHeight = BottomRow.ActualHeight;
        }

        private void Frame_CollapseToggled(object? sender, EventArgs e)
        {
            var frame = (PanelFrame)sender!;
            RememberSlotSizes();
            var state = CurrentLayoutState;
            if (frame.Slot == LayoutSlot.Right)
                state.RightCollapsed = !state.RightCollapsed;
            else if (frame.Slot == LayoutSlot.Bottom)
                state.BottomCollapsed = !state.BottomCollapsed;
            ApplySlotSizes();
            _store.RequestSave();
        }

        private void SlotSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            RememberSlotSizes();
            _store.RequestSave();
        }

        #endregion

        #region Database panel

        private void CollapseDatabasePanel_Click(object sender, RoutedEventArgs e)
        {
            _settings.Layout.DatabasePanelWidth = DatabaseColumn.ActualWidth;
            SetDatabasePanelExpanded(false);
            _store.RequestSave();
        }

        private void ExpandDatabasePanel_Click(object sender, RoutedEventArgs e)
        {
            SetDatabasePanelExpanded(true);
            _store.RequestSave();
        }

        /// <summary>Collapsed: the panel is replaced by a thin strip that expands it again.</summary>
        private void SetDatabasePanelExpanded(bool expanded)
        {
            _settings.Layout.DatabasePanelCollapsed = !expanded;
            DatabasePanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            DatabaseStrip.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            DatabaseSplitter.IsHitTestVisible = expanded;   // the gap stays, see ApplySlotSizes
            DatabaseColumn.MinWidth = expanded ? MinSidePanelWidth : 0;
            DatabaseColumn.Width = expanded
                ? new GridLength(Math.Max(_settings.Layout.DatabasePanelWidth, MinSidePanelWidth))
                : GridLength.Auto;
            DatabasePanelMenu.IsChecked = expanded;
        }

        private void DatabasePanelMenu_Click(object sender, RoutedEventArgs e)
        {
            if (DatabasePanel.Visibility == Visibility.Visible)
                _settings.Layout.DatabasePanelWidth = DatabaseColumn.ActualWidth;
            SetDatabasePanelExpanded(DatabasePanel.Visibility != Visibility.Visible);
            _store.RequestSave();
        }

        private void DatabaseSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _settings.Layout.DatabasePanelWidth = DatabaseColumn.ActualWidth;
            _store.RequestSave();
        }

        #endregion

        #region Database and table list

        private async void OpenDatabase_Click(object sender, RoutedEventArgs e) => await OpenDatabaseInteractiveAsync();

        private async Task OpenDatabaseInteractiveAsync()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Access database (*.mdb;*.accdb)|*.mdb;*.accdb|All files|*.*"
            };
            if (_settings.Session.LastDatabase != null)
                dialog.InitialDirectory = Path.GetDirectoryName(_settings.Session.LastDatabase);
            if (dialog.ShowDialog(this) == true)
                await OpenDatabaseAsync(dialog.FileName, restoreTabs: false);
        }

        private async Task OpenDatabaseAsync(string path, bool restoreTabs)
        {
            // The open tables are about to close
            if (!ConfirmClose(_tabs))
                return;
            StatusText.Text = $"Opening {Path.GetFileName(path)}…";
            try
            {
                var database = new AccessDatabase(path);
                var tables = await Task.Run(database.GetTableNames);

                _database = database;
                _tabs.Clear();
                ShowRow(null);
                _settings.Session.LastDatabase = path;

                NewTableButton.IsEnabled = NewTableMenu.IsEnabled = true;
                DatabasePathText.Text = Path.GetFileName(path);
                DatabasePathText.ToolTip = path;
                TableList.ItemsSource = tables;
                ApplyTableFilter();
                StatusText.Text = $"{Path.GetFileName(path)}: {tables.Count} tables";

                if (restoreTabs)
                    RestoreTabs(tables);
                UpdateTabsHint();
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Empty;
                AppDialog.Error(this, "Open database", $"Couldn't open {Path.GetFileName(path)}", AccessDatabase.ExplainOpenError(ex));
            }
        }

        private void RestoreTabs(IReadOnlyList<string> tables)
        {
            // Only the active tab loads its data now; the others load when first shown
            foreach (var name in _settings.Session.OpenTables.Where(tables.Contains))
            {
                var tab = AddTab(name);
                tab.IsPinned = _settings.Session.PinnedTables.Contains(name);
                tab.IsPreview = !tab.IsPinned && name == _settings.Session.PreviewTable;
            }

            var active = _tabs.FirstOrDefault(t => t.Name == _settings.Session.ActiveTable) ?? _tabs.FirstOrDefault();
            if (active != null)
                TableTabs.SelectedItem = active;
        }

        private void TableFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplyTableFilter();

        private void ApplyTableFilter()
        {
            if (TableList.ItemsSource == null)
                return;

            // Every word, in any order ("0603 res" finds "Resistors 0603"), as in the row search
            var words = SearchText.Words(TableFilter.Text);
            CollectionViewSource.GetDefaultView(TableList.ItemsSource).Filter = words.Count == 0
                ? null
                : item => SearchText.Matches((string)item, words);

            // While filtering, a match is always selected, so Enter has something to open
            if (words.Count > 0 && TableList.SelectedIndex < 0 && TableList.Items.Count > 0)
                SelectTableAt(0);
        }

        /// <summary>
        /// The filter box drives the list from the keyboard: Up / Down move the selection (the cursor stays in the
        /// box, so typing can go on), Enter opens the selected table in a tab of its own, Esc clears the filter.
        /// </summary>
        private void TableFilter_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var count = TableList.Items.Count;
            switch (e.Key)
            {
                case Key.Down or Key.Up when count > 0:
                    var index = TableList.SelectedIndex < 0
                        ? (e.Key == Key.Down ? 0 : count - 1)
                        : Math.Clamp(TableList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, count - 1);
                    SelectTableAt(index);
                    e.Handled = true;
                    break;
                case Key.PageDown or Key.PageUp when count > 0:
                    SelectTableAt(Math.Clamp(Math.Max(TableList.SelectedIndex, 0) + (e.Key == Key.PageDown ? 10 : -10), 0, count - 1));
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if ((TableList.SelectedItem ?? (count > 0 ? TableList.Items[0] : null)) is string name)
                        OpenTable(name, keep: true);
                    e.Handled = true;
                    break;
                case Key.Escape when TableFilter.Text.Length > 0:
                    TableFilter.Clear();
                    e.Handled = true;
                    break;
            }
        }

        private void SelectTableAt(int index)
        {
            TableList.SelectedIndex = index;
            TableList.ScrollIntoView(TableList.SelectedItem);
        }

        /// <summary>Ctrl+F: the cursor goes to the table filter (the database panel opens if it was collapsed).</summary>
        private void FocusTableFilter()
        {
            if (DatabasePanel.Visibility != Visibility.Visible)
            {
                SetDatabasePanelExpanded(true);
                _store.RequestSave();
            }
            TableFilter.Focus();
            TableFilter.SelectAll();
        }

        // A click opens the table in the preview tab; a double-click or Enter keeps it open in a tab of its own

        private void TableList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (TableListItemAt(e) is string name)
                OpenTable(name, keep: !_settings.General.PreviewTabs);
        }

        private void TableList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (TableListItemAt(e) is string name)
                OpenTable(name, keep: true);
        }

        private string? TableListItemAt(MouseEventArgs e) =>
            ItemsControl.ContainerFromElement(TableList, (DependencyObject)e.OriginalSource) is ListBoxItem { Content: string name } ? name : null;

        private void TableList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && TableList.SelectedItem is string name)
                OpenTable(name, keep: true);
            else if (e.Key == Key.Delete && TableList.SelectedItem is string selected)
            {
                e.Handled = true;
                DeleteTable(selected);
            }
        }

        #endregion

        #region New tables and templates

        // The table the list's context menu was opened on (null: on the empty space below the tables)
        private string? _contextTable;

        private void TableList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            _contextTable = Mouse.DirectlyOver is DependencyObject over &&
                            ItemsControl.ContainerFromElement(TableList, over) is ListBoxItem { Content: string name }
                ? name
                : null;
            TableMenuOpen.IsEnabled = TableMenuSaveTemplate.IsEnabled = TableMenuDelete.IsEnabled = _contextTable != null;
            TableMenuNew.IsEnabled = _database != null;
        }

        private void TableMenu_Open(object sender, RoutedEventArgs e)
        {
            if (_contextTable != null)
                OpenTable(_contextTable, keep: true);
        }

        private void NewTable_Click(object sender, RoutedEventArgs e) => ShowNewTable();

        private async void ShowNewTable()
        {
            if (_database == null)
                return;
            var database = _database;
            var dialog = new NewTableWindow(database, (IEnumerable<string>?)TableList.ItemsSource ?? []) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.CreatedTable is not { } name || database != _database)
                return;

            // The new table joins the list (open tabs stay as they are) and opens in a tab
            try
            {
                var tables = await Task.Run(database.GetTableNames);
                if (database != _database)
                    return;
                TableList.ItemsSource = tables;
                ApplyTableFilter();
            }
            catch (Exception ex)
            {
                // The table was created; only the list couldn't be read again (an exception must not end the app here)
                StatusText.Text = ex.Message;
            }
            // A filter that hides the new table is cleared, so it can be seen
            if (!TableList.Items.Contains(name))
                TableFilter.Clear();
            TableList.SelectedItem = name;
            TableList.ScrollIntoView(name);
            if (_settings.Templates.OpenCreatedTable)
                OpenTable(name, keep: true);
            StatusText.Text = $"Created the table \"{name}\" in {Path.GetFileName(database.FilePath)}";
        }

        private async void TableMenu_SaveAsTemplate(object sender, RoutedEventArgs e)
        {
            if (_contextTable is not { } table || _database is not { } database)
                return;
            try
            {
                var fields = await Task.Run(() => database.GetTableFields(table));
                var template = new TableTemplate
                {
                    Name = table,
                    Description = $"Fields of the table \"{table}\" in {Path.GetFileName(database.FilePath)}.",
                    Fields = new ObservableCollection<TemplateField>(fields)
                };
                App.Templates.Add(template);
                new TemplatesWindow(template) { Owner = this }.ShowDialog();
            }
            catch (Exception ex)
            {
                // Database or file errors alike: reported, never left to end the app (this is an async void handler)
                AppDialog.Error(this, "Save as template", $"Couldn't save \"{table}\" as a template", ex.Message);
            }
        }

        private void Templates_Click(object sender, RoutedEventArgs e) => new TemplatesWindow { Owner = this }.ShowDialog();

        private void TableMenu_Delete(object sender, RoutedEventArgs e)
        {
            if (_contextTable != null)
                DeleteTable(_contextTable);
        }

        // One deletion at a time: a second Del while the first is still asking or working is ignored
        private bool _deletingTable;

        /// <summary>
        /// Deletes a table from the database after the user confirmed it (with its row count). A copy of the table is
        /// saved first, so it can be brought back; if that fails the user is asked again. The tab closes and the table
        /// leaves the list only once it is really gone.
        /// </summary>
        private async void DeleteTable(string name)
        {
            if (_database is not { } database || _deletingTable)
                return;
            // Unsaved changes to the table are saved (or dropped) first, so the copy kept in the bin is the table as
            // the user last saw it
            if (_tabs.FirstOrDefault(t => t.Name == name) is { } open && !open.ConfirmClose(this))
                return;
            _deletingTable = true;
            var file = Path.GetFileName(database.FilePath);
            DeletedTable? backup = null;
            try
            {
                var rows = await Task.Run(() => database.CountRows(name));
                if (database != _database)
                    return;
                var contents = rows switch { 0 => "The table is empty.", 1 => "Its 1 row is deleted with it.", _ => $"Its {rows:N0} rows are deleted with it." };
                if (!AppDialog.ConfirmDestructive(this, "Delete table", $"Delete the table \"{name}\" from {file}?", "Delete table",
                        $"{contents} Altium components that come from this table can no longer be placed or updated from the database.\n\n" +
                        "A copy of the table is kept in Tools → Deleted tables, where you can restore it.",
                        // The table can be restored, so Enter deletes (the "Delete anyway" without a copy below keeps Cancel)
                        focusAction: true))
                    return;

                StatusText.Text = $"Saving a copy of \"{name}\"…";
                try
                {
                    backup = await Task.Run(() => DeletedTables.Save(database, name));
                }
                catch (Exception ex)
                {
                    StatusText.Text = string.Empty;
                    if (!AppDialog.ConfirmDestructive(this, "Delete table", $"Couldn't save a copy of \"{name}\"", "Delete anyway",
                            $"{ex.Message}\n\nWithout the copy, deleting the table can't be undone."))
                        return;
                }

                await Task.Run(() => database.DeleteTable(name));
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Empty;
                AppDialog.Error(this, "Delete table", $"Couldn't delete \"{name}\"", ex.Message);
                return;
            }
            finally
            {
                _deletingTable = false;
            }
            if (database != _database)
                return;

            if (_tabs.FirstOrDefault(t => t.Name == name) is { } tab)
                CloseTabs([tab]);
            _settings.Tables.ColumnWidths.Remove(name);
            _settings.Tables.SearchColumns.Remove(name);
            _store.RequestSave();
            if (TableList.ItemsSource is IEnumerable<string> tables)
            {
                TableList.ItemsSource = tables.Where(t => t != name).ToList();
                ApplyTableFilter();
            }
            StatusText.Text = backup == null
                ? $"Deleted the table \"{name}\" from {file}"
                : $"Deleted the table \"{name}\" from {file} (restore it from Tools → Deleted tables)";
        }

        /// <summary>The bin of deleted tables; tables restored there join the list, the last one opens in a tab.</summary>
        private async void DeletedTables_Click(object sender, RoutedEventArgs e)
        {
            var database = _database;
            var dialog = new DeletedTablesWindow(database, (IEnumerable<string>?)TableList.ItemsSource ?? []) { Owner = this };
            dialog.ShowDialog();
            if (dialog.RestoredTables.Count == 0 || database == null || database != _database)
                return;

            try
            {
                var tables = await Task.Run(database.GetTableNames);
                if (database != _database)
                    return;
                TableList.ItemsSource = tables;
                ApplyTableFilter();
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
                return;
            }
            var name = dialog.RestoredTables[^1];
            if (!TableList.Items.Contains(name))
                TableFilter.Clear();
            TableList.SelectedItem = name;
            TableList.ScrollIntoView(name);
            OpenTable(name, keep: true);
            StatusText.Text = dialog.RestoredTables.Count == 1
                ? $"Restored the table \"{name}\""
                : $"Restored {dialog.RestoredTables.Count} tables";
        }

        #endregion

        #region Table tabs

        /// <summary>
        /// Activates the table's tab, opening it if needed. Unless <paramref name="keep"/> is set, a table that isn't
        /// open yet goes into the preview tab, replacing the table previewed before (or into a new preview tab right
        /// after the active one), so browsing through tables doesn't pile up tabs.
        /// </summary>
        private void OpenTable(string name, bool keep)
        {
            var tab = _tabs.FirstOrDefault(t => t.Name == name);
            if (tab == null)
            {
                tab = CreateTab(name);
                tab.IsPreview = !keep;
                var preview = keep ? null : _tabs.FirstOrDefault(t => t.IsPreview);
                if (preview != null)
                    _tabs[_tabs.IndexOf(preview)] = tab;
                else
                {
                    // After the active tab, but never among the pinned ones
                    var index = TableTabs.SelectedItem is TableTab active ? _tabs.IndexOf(active) + 1 : _tabs.Count;
                    _tabs.Insert(Math.Max(index, _tabs.Count(t => t.IsPinned)), tab);
                }
                UpdateTabsHint();
            }
            else if (keep)
                tab.IsPreview = false;
            TableTabs.SelectedItem = tab;
        }

        private TableTab AddTab(string name)
        {
            var tab = CreateTab(name);
            _tabs.Add(tab);
            UpdateTabsHint();
            return tab;
        }

        private TableTab CreateTab(string name)
        {
            var tab = new TableTab(name);
            tab.SelectedRowChanged += () =>
            {
                if (TableTabs.SelectedItem == tab)
                    ShowRow(tab.SelectedRow);
            };
            // Open / choose in the table's context menu: the same as in the details, for the selected row
            tab.FieldActionRequested += (name, action, placement) =>
            {
                if (TableTabs.SelectedItem != tab ||
                    _detailsFields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } field)
                    return;
                if (action == FieldAction.Open)
                    OpenFieldValue(field);
                else
                    BrowseField(field, placement);
            };
            // Changed, saved or deleted rows: the details and preview show the selected row as it is now (in place
            // when it is still the row shown, so a value being worked on in the details keeps its place)
            tab.RowsChanged += () =>
            {
                if (TableTabs.SelectedItem != tab)
                    return;
                if (tab.SelectedRow?.Row is { } selected && selected == _detailsRow)
                    RefreshDetails(selected);
                else
                    ShowRow(tab.SelectedRow);
            };
            tab.RowValuesChanged += row =>
            {
                if (TableTabs.SelectedItem == tab)
                    RefreshDetails(row);
            };
            // Saving, undoing, closing…: a value being typed in the details for this table is applied first
            tab.FinishingEdits += () =>
            {
                if (TableTabs.SelectedItem == tab)
                    CommitDetailsEdit();
            };
            // A cell of another column in the table: the details select its field too
            tab.CurrentFieldChanged += name =>
            {
                if (TableTabs.SelectedItem != tab)
                    return;
                _linkedField = name;
                SelectDetailsField(name);
            };
            tab.Status += message =>
            {
                if (TableTabs.SelectedItem == tab)
                    StatusText.Text = message;
            };
            tab.ReloadRequested += () => ReloadTab(tab);
            tab.AddElementsRequested += () =>
            {
                if (_database is { } database)
                    new AddElementsWindow(tab, database, _renderService) { Owner = this }.ShowDialog();
            };
            // Unsaved changes (the tab's star) and the history: the title bar buttons and menus follow the active tab
            tab.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(TableTab.IsDirty) && tab.IsDirty)
                    tab.IsPreview = false;   // a changed table isn't replaced by the next one previewed
                if (e.PropertyName is nameof(TableTab.IsEditing) && TableTabs.SelectedItem == tab)
                    UpdateDetailsBrowse();
                if (e.PropertyName is nameof(TableTab.IsDirty) or nameof(TableTab.CanUndo) or nameof(TableTab.CanRedo)
                    or nameof(TableTab.UndoDescription) or nameof(TableTab.IsEditing) or nameof(TableTab.CanEdit))
                    UpdateTableCommands();
            };
            return tab;
        }

        /// <summary>
        /// Before these tabs close (or the database or the app does): each one with unsaved changes asks whether to save
        /// them. False if the user cancelled (or a save failed); that tab is then shown, and the caller stops.
        /// </summary>
        private bool ConfirmClose(IEnumerable<TableTab> tabs)
        {
            foreach (var tab in tabs.ToList())
            {
                if (tab.IsDirty)
                    TableTabs.SelectedItem = tab;
                if (tab.ConfirmClose(this))
                    continue;
                TableTabs.SelectedItem = tab;
                return false;
            }
            return true;
        }

        /// <summary>F5 / the Reload button: reads the table from the database again (changes made in Access or
        /// Altium show up), keeping the search. Unsaved changes are saved or dropped first, as the user chooses.</summary>
        private void ReloadTab(TableTab tab)
        {
            if (tab.Loading is { IsCompleted: false } || !tab.ConfirmClose(this))
                return;
            tab.Loading = LoadTabAsync(tab);
        }

        private void TableTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // The DataGrids inside the tabs raise SelectionChanged too, and it bubbles up here
            if (e.OriginalSource != TableTabs)
                return;

            UpdateTableCommands();
            // A row being edited is kept when its tab is left; if it can't be and the user keeps editing, back to it
            foreach (var left in e.RemovedItems.OfType<TableTab>())
            {
                if (_tabs.Contains(left) && !left.TryFinishEditing(this))
                {
                    Dispatcher.BeginInvoke(() => TableTabs.SelectedItem = left);
                    return;
                }
            }

            if (TableTabs.SelectedItem is not TableTab tab)
            {
                ShowRow(null);
                return;
            }

            if (tab.Table == null)
                tab.Loading ??= LoadTabAsync(tab);
            else
                StatusText.Text = tab.IsDirty ? $"{tab.Name}: {tab.CountText}, with unsaved changes (Ctrl+S saves)" : $"{tab.Name}: {tab.CountText}";
            ShowRow(tab.SelectedRow);
            // The same field stays marked in the table switched to (if it has one by that name)
            if (_linkedField != null && tab.Table != null)
                tab.ShowField(_linkedField);

            // Scroll the tab strip so the active tab is visible (after layout, when its container exists)
            Dispatcher.BeginInvoke(() =>
                (TableTabs.ItemContainerGenerator.ContainerFromItem(tab) as FrameworkElement)?.BringIntoView(),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        #region Tab strip scrolling

        private T TabStripPart<T>(string name) where T : class => (T)TableTabs.Template.FindName(name, TableTabs);

        private void TabScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Arrows and the tab list appear only when the tabs don't fit
            var scroller = (ScrollViewer)sender;
            var overflow = scroller.ScrollableWidth > 0.5;
            TabStripPart<FrameworkElement>("TabNavigation").Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
            // Dimmed rather than disabled: a disabled Fluent button paints a light box, which stands out in the dark theme
            SetArrowActive(TabStripPart<UIElement>("ScrollTabsLeft"), scroller.HorizontalOffset > 0.5);
            SetArrowActive(TabStripPart<UIElement>("ScrollTabsRight"), scroller.HorizontalOffset < scroller.ScrollableWidth - 0.5);
        }

        private static void SetArrowActive(UIElement arrow, bool active)
        {
            arrow.Opacity = active ? 1 : 0.3;
            arrow.IsHitTestVisible = active;
        }

        private void ScrollTabsLeft_Click(object sender, RoutedEventArgs e) => ScrollTabs(-1);

        private void ScrollTabsRight_Click(object sender, RoutedEventArgs e) => ScrollTabs(1);

        private void ScrollTabs(int direction)
        {
            var scroller = TabStripPart<ScrollViewer>("TabScroller");
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + direction * scroller.ViewportWidth * 0.6);
        }

        private void TabListButton_Click(object sender, RoutedEventArgs e)
        {
            var button = (UIElement)sender;
            if (DropDown.WasJustClosed(button))
                return;
            // The window's font: otherwise the menu inherits the icon font of the button it opens from and shows boxes
            var menu = new ContextMenu
            {
                PlacementTarget = button,
                Placement = PlacementMode.Bottom,
                FontFamily = FontFamily,
                FontSize = FontSize
            };
            foreach (var tab in _tabs)
            {
                // TextBlock header: a plain string would turn '_' into an access key
                var item = new MenuItem
                {
                    Header = new TextBlock { Text = tab.IsPinned ? $"{tab.Name}  (pinned)" : tab.Name },
                    IsChecked = TableTabs.SelectedItem == tab
                };
                item.Click += (_, _) => TableTabs.SelectedItem = tab;
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
            DropDown.Track(menu, button);
        }

        #endregion

        private async Task LoadTabAsync(TableTab tab)
        {
            var database = _database;
            if (database == null)
                return;

            StatusText.Text = $"Loading table {tab.Name}…";
            try
            {
                var (table, fields) = await Task.Run(() =>
                {
                    var rows = database.LoadTable(tab.Name);
                    // Types and key, for editing; without them the table is still shown, read-only
                    List<TemplateField>? schema;
                    try
                    {
                        schema = database.GetTableFields(tab.Name);
                    }
                    catch (Exception)
                    {
                        schema = null;
                    }
                    return (rows, schema);
                });
                if (!_tabs.Contains(tab) || database != _database)
                    return;
                // Reloading: the table was changed while it was being read (its changes were saved or dropped before):
                // the new changes are kept rather than replaced
                if (tab.Table != null && tab.HasPendingChanges)
                {
                    tab.Loading = null;
                    if (TableTabs.SelectedItem == tab)
                        StatusText.Text = $"{tab.Name} wasn't reloaded: it was changed while it was being read. Save it, then reload (F5).";
                    return;
                }

                var firstRead = tab.Table == null;
                tab.SetTable(table, fields, database);
                if (firstRead && _settings.Editing.EditOnOpen && !tab.IsPreview && tab.CanEdit)
                    tab.IsEditing = true;
                if (TableTabs.SelectedItem == tab)
                    ShowRow(tab.SelectedRow);
                if (_linkedField != null)
                    tab.ShowField(_linkedField);
                if (TableTabs.SelectedItem == tab)
                    StatusText.Text = $"{tab.Name}: {table.Rows.Count} rows";
            }
            catch (Exception ex)
            {
                tab.Loading = null;   // allow retrying by activating the tab again
                if (TableTabs.SelectedItem == tab)
                    StatusText.Text = $"Failed to read table {tab.Name}: {ex.Message}";
            }
        }

        private void CloseTabs(IEnumerable<TableTab> tabs)
        {
            var toClose = tabs.ToList();
            if (toClose.Count == 0 || !ConfirmClose(toClose))
                return;

            var selected = TableTabs.SelectedItem as TableTab;
            var selectedIndex = selected == null ? 0 : _tabs.IndexOf(selected);
            foreach (var tab in toClose)
                _tabs.Remove(tab);

            // If the active tab was closed, activate its neighbour
            if (selected != null && !_tabs.Contains(selected) && _tabs.Count > 0)
                TableTabs.SelectedItem = _tabs[Math.Min(selectedIndex, _tabs.Count - 1)];
            if (_tabs.Count == 0)
            {
                ShowRow(null);
                // Not the row count of a table that is no longer open
                StatusText.Text = _database == null ? string.Empty : Path.GetFileName(_database.FilePath);
            }
            UpdateTabsHint();
        }

        /// <summary>Pinned tabs are kept together at the start of the strip.</summary>
        private void TogglePin(TableTab tab)
        {
            var index = _tabs.IndexOf(tab);
            tab.IsPinned = !tab.IsPinned;
            tab.IsPreview = false;
            // Right after the other pinned tabs: the end of the pinned group when pinning,
            // the start of the unpinned group when unpinning
            var target = _tabs.Count(t => t.IsPinned && t != tab);
            if (index != target)
                _tabs.Move(index, target);
            TableTabs.SelectedItem = tab;
        }

        private void UpdateTabsHint() =>
            NoTabsHint.Visibility = _tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private static TableTab TabOf(object sender) => (TableTab)((FrameworkElement)sender).DataContext;

        private void TabCloseButton_Click(object sender, RoutedEventArgs e) => CloseTabs([TabOf(sender)]);

        private void TabPinButton_Click(object sender, RoutedEventArgs e) => TogglePin(TabOf(sender));

        private void TabMenu_TogglePin(object sender, RoutedEventArgs e) => TogglePin(TabOf(sender));

        private void TabMenu_Close(object sender, RoutedEventArgs e) => CloseTabs([TabOf(sender)]);

        /// <summary>Ctrl+W / Ctrl+F4. A pinned tab stays (it has no close button either) until it is unpinned.</summary>
        private void CloseActiveTab()
        {
            if (TableTabs.SelectedItem is not TableTab tab)
                return;
            if (tab.IsPinned)
            {
                StatusText.Text = $"\"{tab.Name}\" is pinned: unpin it to close it";
                return;
            }
            CloseTabs([tab]);
        }

        private void TabMenu_CloseOthers(object sender, RoutedEventArgs e)
        {
            var tab = TabOf(sender);
            CloseTabs(_tabs.Where(t => t != tab && !t.IsPinned));
            TableTabs.SelectedItem = tab;
        }

        private void TabMenu_CloseToRight(object sender, RoutedEventArgs e)
        {
            var index = _tabs.IndexOf(TabOf(sender));
            CloseTabs(_tabs.Skip(index + 1).Where(t => !t.IsPinned));
        }

        private void TabMenu_CloseUnpinned(object sender, RoutedEventArgs e) => CloseTabs(_tabs.Where(t => !t.IsPinned));

        // Drag a tab header onto another one to reorder; middle-click closes an unpinned tab

        private Point _dragStart;
        private TableTab? _dragTab;

        private void TabHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(this);
            _dragTab = TabOf(sender);
            // Double-clicking a preview tab keeps it
            if (e.ClickCount == 2)
                _dragTab.IsPreview = false;

            // The tab is selected here instead of by the TabItem: its click also moves keyboard focus into the table,
            // whose first visible row (usually cut off at the top) then got scrolled fully into view, so the table
            // crept up a row. The pin and close buttons still get their clicks.
            if (!IsInButton(e.OriginalSource as DependencyObject, (DependencyObject)sender))
            {
                TableTabs.SelectedItem = _dragTab;
                e.Handled = true;
            }
        }

        private static bool IsInButton(DependencyObject? element, DependencyObject container)
        {
            for (var current = element; current != null && current != container; current = VisualTreeHelper.GetParent(current))
                if (current is ButtonBase)
                    return true;
            return false;
        }

        private void TabHeader_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragTab == null || e.LeftButton != MouseButtonState.Pressed)
                return;

            var delta = e.GetPosition(this) - _dragStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            var tab = _dragTab;
            _dragTab = null;
            DragDrop.DoDragDrop((DependencyObject)sender, tab, DragDropEffects.Move);
        }

        private void TabHeader_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(TableTab)) is not TableTab source)
                return;

            var target = TabOf(sender);
            // Pinned and unpinned tabs stay in their own groups
            if (source == target || source.IsPinned != target.IsPinned)
                return;

            _tabs.Move(_tabs.IndexOf(source), _tabs.IndexOf(target));
            source.IsPreview = false;
            TableTabs.SelectedItem = source;
        }

        private void TabHeader_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _dragTab = null;
            if (e.ChangedButton == MouseButton.Middle && TabOf(sender) is { IsPinned: false } tab)
                CloseTabs([tab]);
        }

        #endregion

        #region Selected row

        /// <summary>Shows the row's fields in the details panel and its symbol/footprint/3D in the preview.</summary>
        private void ShowRow(DataRowView? rowView)
        {
            // A value being typed in the details belongs to the row shown so far: applied to it (dropped if it can't be)
            CommitDetailsEdit();
            RefPicker.IsOpen = false;
            if (rowView == null)
            {
                _detailsRow = null;
                _previewKey = null;
                DetailsGrid.ItemsSource = null;
                DetailsFrame.Title = "Details";
                // Don't leave the previous tab's component in the preview
                Preview.Clear();
                return;
            }

            var row = rowView.Row;
            var table = row.Table;

            // Altium fields first, then the rest in table order
            var leading = TableTab.LeadingColumns
                .Select(name => AccessDatabase.FindColumn(table, name))
                .OfType<DataColumn>()
                .ToList();
            var columns = leading.Concat(TableTab.FieldColumns(table).Except(leading));
            _detailsRow = row;
            _detailsFields = columns.Select(c => new RowField(c.ColumnName, GetValue(row, c) ?? "",
                c.DataType == typeof(string) ? FieldLinks.Classify(c.ColumnName) : FieldLink.None)).ToList();
            UpdateDetailsBrowse();
            DetailsGrid.ItemsSource = _detailsFields;
            DetailsFrame.Title = $"Details: {GetValue(row, leading.FirstOrDefault() ?? table.Columns[0])}";
            if (_linkedField != null)
                SelectDetailsField(_linkedField);
            _previewKey = null;
            UpdatePreview(row);
        }

        #region Paths and links in the details

        /// <summary>The buttons that pick a file work while the fields can be changed.</summary>
        private void UpdateDetailsBrowse()
        {
            var tab = ActiveTab;
            var inTable = _detailsRow is { RowState: not (DataRowState.Detached or DataRowState.Deleted) };
            foreach (var field in _detailsFields)
                field.CanBrowse = inTable && tab != null && tab.CanChangeField(field.Name);
        }

        /// <summary>Picks a file for a path or link field, with the file types it usually holds. Like a typed value, it
        /// goes through the table's history.</summary>
        private void DetailsBrowse_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is RowField field && !DropDown.WasJustClosed((UIElement)sender))
                BrowseField(field, (FrameworkElement)sender);
        }

        private void BrowseField(RowField field, FrameworkElement placement)
        {
            if (_database is not { } database || ActiveTab is not { } tab || _detailsRow is not { } row || !tab.TryFinishEditing(this))
                return;
            if (FieldLinks.IsRef(field.Link))
            {
                ShowRefPicker(field, placement, tab, row, database);
                return;
            }

            var databaseFolder = Path.GetDirectoryName(database.FilePath)!;
            var dialog = new OpenFileDialog { Title = $"Choose a file for {field.Name}", Filter = FieldLinks.Filter(field.Link) };
            dialog.InitialDirectory = FolderOf(field.Value, databaseFolder);
            if (dialog.ShowDialog(this) != true || row != _detailsRow || tab != ActiveTab)
                return;

            // Links are opened as they are, by whatever reads them: only library paths are stored relative to the database
            var example = field.Value.Length > 0 ? field.Value
                : _detailsFields.FirstOrDefault(f => f.Link is FieldLink.Symbol or FieldLink.Footprint && f.Value.Length > 0)?.Value;
            ApplyPickedFile(tab, row, field, field.Link == FieldLink.Document
                ? dialog.FileName
                : FieldLinks.StoredPath(dialog.FileName, databaseFolder, example));
        }

        private void ApplyPickedFile(TableTab tab, DataRow row, RowField field, string value)
        {
            if (tab.ChangeField(row, field.Name, value) is { } error)
                AppDialog.Error(this, "Change field", $"Couldn't change {field.Name}", error);
            else
                RefreshDetails(row);
        }

        // The Ref field the picker is open for, and the components it offers
        private (TableTab Tab, DataRow Row, RowField Field)? _refTarget;
        private IReadOnlyList<string> _refNames = [];
        private int _refRequest;

        /// <summary>The library a Ref field's value comes from, as the row has it now (a path just picked included).</summary>
        private static string? LibraryOf(DataRow row, RowField field) =>
            FieldLinks.LibraryFields(field.Name, field.Link)
                .Select(name => GetValue(row, AccessDatabase.FindColumn(row.Table, name)))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        /// <summary>
        /// Lists the components of the field's library, to pick its value from. The library is read in the background
        /// (a large PcbLib takes a moment); a row changed meanwhile, or a later click, drops the result.
        /// </summary>
        private async void ShowRefPicker(RowField field, FrameworkElement button, TableTab tab, DataRow row, AccessDatabase database)
        {
            var request = ++_refRequest;
            var pathField = FieldLinks.LibraryFields(field.Name, field.Link)[0];
            if (LibraryOf(row, field) is not { } library)
            {
                StatusText.Text = $"Fill in {pathField} first: {field.Name} is chosen from that library";
                return;
            }

            var wanted = field.Link == FieldLink.SymbolRef ? LibraryKind.SchLib : LibraryKind.PcbLib;
            StatusText.Text = $"Reading {Path.GetFileName(library)}…";
            var service = _renderService;
            var (names, problem) = await Task.Run(() => service.ListLibrary(database, library, wanted));
            if (request != _refRequest || row != _detailsRow || tab != ActiveTab)
                return;

            StatusText.Text = string.Empty;
            if (names == null)
            {
                AppDialog.Error(this, "Choose component", $"Can't list {field.Name}", problem);
                return;
            }

            _refTarget = (tab, row, field);
            _refNames = names;
            RefSource.Text = $"{names.Count} in {Path.GetFileName(library)}";
            RefSource.ToolTip = library;
            RefFilter.Text = "";
            ShowRefNames();
            RefList.SelectedItem = names.FirstOrDefault(n => string.Equals(n, field.Value.Trim(), StringComparison.OrdinalIgnoreCase));
            if (RefList.SelectedItem != null)
                RefList.ScrollIntoView(RefList.SelectedItem);

            // Under the button, its right edge at the button's
            RefPicker.PlacementTarget = button;
            RefPicker.HorizontalOffset = button.ActualWidth - 360;
            RefPicker.Closed -= RefPicker_Closed;
            RefPicker.Closed += RefPicker_Closed;
            RefPicker.IsOpen = true;
            _ = Dispatcher.BeginInvoke(() => RefFilter.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // A second click on the arrow that opened the list closes it
        private void RefPicker_Closed(object? sender, EventArgs e)
        {
            if (RefPicker.PlacementTarget is { } button)
                DropDown.NoteClosed(button);
        }

        private void ShowRefNames()
        {
            var words = SearchText.Words(RefFilter.Text);
            RefList.ItemsSource = words.Count == 0 ? _refNames : _refNames.Where(n => SearchText.Matches(n, words)).ToList();
            if (words.Count > 0 && RefList.Items.Count > 0)
                RefList.SelectedIndex = 0;
        }

        private void RefFilter_TextChanged(object sender, TextChangedEventArgs e) => ShowRefNames();

        // As in the other filters: the cursor stays in the box while Up / Down pick
        private void RefFilter_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var count = RefList.Items.Count;
            switch (e.Key)
            {
                case Key.Down or Key.Up or Key.PageDown or Key.PageUp when count > 0:
                    var step = e.Key switch { Key.Down => 1, Key.Up => -1, Key.PageDown => 10, _ => -10 };
                    RefList.SelectedIndex = RefList.SelectedIndex < 0 ? 0 : Math.Clamp(RefList.SelectedIndex + step, 0, count - 1);
                    RefList.ScrollIntoView(RefList.SelectedItem);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    ApplyRef(RefList.SelectedItem as string);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    RefPicker.IsOpen = false;
                    e.Handled = true;
                    break;
            }
        }

        private void RefList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (ItemsControl.ContainerFromElement(RefList, (DependencyObject)e.OriginalSource) is ListBoxItem { Content: string name })
                ApplyRef(name);
        }

        private void ApplyRef(string? name)
        {
            RefPicker.IsOpen = false;
            if (name != null && _refTarget is { } target && target.Row == _detailsRow)
                ApplyPickedFile(target.Tab, target.Row, target.Field, name);
        }

        // Where the file dialog starts: the folder of the current value when it is there (no search, it has to be quick)
        private static string FolderOf(string value, string databaseFolder)
        {
            try
            {
                var path = FieldLinks.LocalPath(value);
                if (path.Length > 0 && FieldLinks.WebAddress(path) == null &&
                    Path.GetDirectoryName(Path.GetFullPath(Path.Combine(databaseFolder, path))) is { } folder && !folder.StartsWith(@"\\", StringComparison.Ordinal) && Directory.Exists(folder))
                    return folder;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                // Not a path
            }
            return databaseFolder;
        }

        private static RowField? FieldToOpenAt(object source, DataGridColumn valueColumn)
        {
            for (var current = source as DependencyObject; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is DataGridCell cell)
                    return cell.Column == valueColumn && !cell.IsEditing && cell.DataContext is RowField field && CanOpen(field) ? field : null;
                // Parts of text aren't visuals, whose parents can be asked for
                if (current is not Visual)
                    return null;
            }
            return null;
        }

        private static bool CanOpen(RowField field) => FieldLinks.CanOpen(field.Value, field.Link);

        /// <summary>Ctrl+click on a value opens it (see <see cref="OpenFieldValue"/>).</summary>
        private void DetailsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && FieldToOpenAt(e.OriginalSource, DetailsValueColumn) is { } field)
            {
                e.Handled = true;
                OpenFieldValue(field);
            }
        }

        // The hand shows what Ctrl+click would open
        private void DetailsGrid_PreviewMouseMove(object sender, MouseEventArgs e) =>
            DetailsGrid.Cursor = Keyboard.Modifiers == ModifierKeys.Control && FieldToOpenAt(e.OriginalSource, DetailsValueColumn) != null
                ? Cursors.Hand
                : null;

        /// <summary>
        /// Opens what a value points to. It comes from the database, so it is never run: a web address goes to the
        /// browser (http and https only), a document to its program, and any other file (a library, or a program a
        /// value might name) is shown selected in its folder.
        /// </summary>
        private async void OpenFieldValue(RowField field)
        {
            var value = field.Value;
            try
            {
                if (FieldLinks.WebAddress(value) is { } address)
                {
                    Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
                    return;
                }
                if (_database is not { } database)
                    return;

                // In the background: it can search the folders, or wait for a network drive
                var path = FieldLinks.LocalPath(value);
                var (file, folder) = await Task.Run(() =>
                {
                    var found = database.ResolveLibraryPath(path);
                    var combined = found == null ? Path.Combine(Path.GetDirectoryName(database.FilePath)!, path) : null;
                    return (found, combined != null && Directory.Exists(combined) ? Path.GetFullPath(combined) : null);
                });

                if (file != null && field.Link == FieldLink.Document && FieldLinks.IsDocument(file))
                    Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                else if (file != null)
                    // A path that exists can't contain a quote
                    Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{file}\"" });
                else if (folder != null)
                    Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder } });
                else
                    StatusText.Text = $"Not found: {value}";
            }
            catch (Exception ex)
            {
                // An async void handler: reported here, never left to end the app
                StatusText.Text = $"Couldn't open {value}: {ex.Message}";
            }
        }

        #endregion

        #region Field shown in both the table and the details

        // The field selected in either place: a cell's column in the table, a field in the details. Kept across rows and
        // tabs, so moving to another row keeps the same field marked.
        private string? _linkedField;
        private bool _selectingDetailsField;

        /// <summary>Selects a field in the details (scrolled into view), without moving the keyboard there.</summary>
        private void SelectDetailsField(string name)
        {
            if (_detailsFields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } field ||
                DetailsGrid.SelectedItem == field)
                return;
            _selectingDetailsField = true;
            try
            {
                DetailsGrid.SelectedItem = field;
                DetailsGrid.ScrollIntoView(field);
            }
            finally
            {
                _selectingDetailsField = false;
            }
        }

        /// <summary>A field picked in the details: the table marks it too (its cell in the selected row).</summary>
        private void DetailsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selectingDetailsField || e.OriginalSource != DetailsGrid || DetailsGrid.SelectedItem is not RowField field)
                return;
            _linkedField = field.Name;
            ActiveTab?.ShowField(field.Name);
        }

        #endregion

        // The row the details show, and its fields as shown
        private DataRow? _detailsRow;
        private List<RowField> _detailsFields = [];
        // What the preview shows (symbol and footprints), so it is redrawn only when those change
        private string? _previewKey;
        private bool _detailsRefreshQueued;

        private void UpdatePreview(DataRow row)
        {
            if (_database == null)
                return;
            var table = row.Table;
            // Standard Altium DbLib field names
            var symbolRef = GetValue(row, AccessDatabase.FindColumn(table, "Library Ref", "LibRef", "Symbol Ref"));
            var symbolPath = GetValue(row, AccessDatabase.FindColumn(table, "Library Path", "LibPath", "Symbol Path"));
            var footprints = GetFootprints(row);
            var key = string.Join("\u0001", new[] { symbolRef, symbolPath }.Concat(footprints.SelectMany(f => new[] { f.Ref, f.Path })));
            if (key == _previewKey)
                return;
            _previewKey = key;
            Preview.ShowComponent(new PreviewRequest(symbolPath, symbolRef, footprints, _database.ResolveLibraryPath));
        }

        /// <summary>
        /// The shown row's values changed (a cell left in the table, an undo, a value typed in the details): the
        /// details show them in place, keeping their scroll position, and the preview follows a changed symbol or
        /// footprint. Several changes at once (an undo sets every field) are shown once.
        /// </summary>
        private void RefreshDetails(DataRow row)
        {
            if (row != _detailsRow || _detailsRefreshQueued)
                return;
            _detailsRefreshQueued = true;
            Dispatcher.BeginInvoke(() =>
            {
                _detailsRefreshQueued = false;
                if (_detailsRow is not { } shown || shown.RowState is DataRowState.Deleted ||
                    shown.RowState == DataRowState.Detached && !IsBeingAdded(shown))
                    return;
                foreach (var field in _detailsFields)
                    if (shown.Table.Columns[field.Name] is { } column)
                        field.Value = GetValue(shown, column) ?? "";
                if (_detailsFields.Count > 0)
                    DetailsFrame.Title = $"Details: {_detailsFields[0].Value}";
                UpdatePreview(shown);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        // A new row being typed in the table: not in the table yet, but it has values
        private bool IsBeingAdded(DataRow row) =>
            ActiveTab?.Grid.Items is IEditableCollectionView { CurrentAddItem: DataRowView added } && added.Row == row;

        private bool _committingDetails;

        /// <summary>
        /// Applies the value being typed in the details, if any (before a save, an undo, closing, another row): as if
        /// Enter was pressed. If it can't be applied (the reason is shown), it is dropped rather than left open for a
        /// row or table that is going away.
        /// </summary>
        private void CommitDetailsEdit()
        {
            // Applying it saves through the table, which asks for this again
            if (_committingDetails || !IsDetailsEditing())
                return;
            _committingDetails = true;
            try
            {
                if (!DetailsGrid.CommitEdit(DataGridEditingUnit.Cell, true))
                    DetailsGrid.CancelEdit(DataGridEditingUnit.Cell);
                DetailsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            }
            finally
            {
                _committingDetails = false;
            }
        }

        // A details cell shows its text box only while being edited
        private bool IsDetailsEditing() =>
            DetailsGrid.CurrentCell is { Column: { } column, Item: { } item } &&
            DetailsGrid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row && column.GetCellContent(row) is TextBox;

        /// <summary>Typing in the details: only in the Value column, while the table's Edit is on, for fields Access
        /// doesn't fill itself, and for a row that is in the table (not one being added in the grid).</summary>
        private void DetailsGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
        {
            var tab = ActiveTab;
            if (e.Column == DetailsValueColumn && e.Row.Item is RowField field && tab != null && tab.CanChangeField(field.Name) &&
                _detailsRow is { RowState: not (DataRowState.Detached or DataRowState.Deleted) })
            {
                // A row being edited in the table is ended first (asking now if it can't be kept), not halfway
                // through applying this value
                if (!tab.TryFinishEditing(this))
                    e.Cancel = true;
                return;
            }
            e.Cancel = true;
            if (tab is { CanEdit: true, IsEditing: false } && e.Column == DetailsValueColumn)
                StatusText.Text = "Turn on Edit (Ctrl+E) to change values";
        }

        /// <summary>A value typed in the details goes into the row as one step of the table's history (Ctrl+Z undoes
        /// it, Ctrl+S saves it); one that can't be kept stays in editing with the reason shown.</summary>
        private void DetailsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not RowField field ||
                e.EditingElement is not TextBox box || ActiveTab is not { } tab || _detailsRow is not { } row || box.Text == field.Value)
                return;
            if (tab.ChangeField(row, field.Name, box.Text) is { } error)
            {
                e.Cancel = true;
                Dispatcher.BeginInvoke(() =>
                {
                    // Still open (Enter was pressed), or dropped (applied on the way to a save, another row, …)
                    var open = IsDetailsEditing();
                    AppDialog.Error(this, "Edit value", $"Couldn't change {field.Name}", open
                        ? $"{error}\n\nCorrect it and press Enter, or press Esc to keep the old value."
                        : $"{error}\n\nThe field keeps its old value.");
                    if (open)
                        box.Focus();
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            else
                RefreshDetails(row);   // shown as the row has it ("5.0" typed: 5), even if that changed nothing
        }

        private static readonly Regex FootprintRefColumn = new(@"^Footprint\s*Ref\s*(\d+)$", RegexOptions.IgnoreCase);

        /// <summary>
        /// The component's footprints: the main one (Footprint Ref / Footprint Path), then the alternatives
        /// Footprint Ref 2, 3, … in order. An alternative without its own path is looked for in the main footprint's
        /// library; empty and repeated entries are left out.
        /// </summary>
        private static List<FootprintChoice> GetFootprints(DataRow row)
        {
            var table = row.Table;
            var mainRef = GetValue(row, AccessDatabase.FindColumn(table, "Footprint Ref", "Footprint", "Footprint Ref 1"));
            var mainPath = GetValue(row, AccessDatabase.FindColumn(table, "Footprint Path", "Footprint Path 1"));
            var footprints = new List<FootprintChoice> { new(mainRef, mainPath) };

            var alternatives = TableTab.FieldColumns(table)
                .Select(c => (column: c, match: FootprintRefColumn.Match(c.ColumnName)))
                .Where(c => c.match.Success)
                .Select(c => (c.column, number: int.TryParse(c.match.Groups[1].Value, out var n) ? n : 0))
                .Where(c => c.number >= 2)
                .OrderBy(c => c.number);
            foreach (var (column, number) in alternatives)
            {
                var name = GetValue(row, column);
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var path = GetValue(row, AccessDatabase.FindColumn(table, $"Footprint Path {number}"));
                var choice = new FootprintChoice(name, string.IsNullOrWhiteSpace(path) ? mainPath : path);
                if (!footprints.Any(f => string.Equals(f.Ref, choice.Ref, StringComparison.OrdinalIgnoreCase) &&
                                         string.Equals(f.Path, choice.Path, StringComparison.OrdinalIgnoreCase)))
                    footprints.Add(choice);
            }
            return footprints;
        }

        private static string? GetValue(DataRow row, DataColumn? column) =>
            column == null || row.IsNull(column) ? null : Convert.ToString(row[column]);

        #endregion
    }
}
