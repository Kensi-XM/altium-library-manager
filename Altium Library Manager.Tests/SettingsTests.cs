using System.Data;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Altium_Library_Manager.Services.Settings;
using static Altium_Library_Manager.Tests.T;
using static Altium_Library_Manager.Tests.TableTests;

namespace Altium_Library_Manager.Tests
{
    /// <summary>No settings file and no combination of options may break the app.</summary>
    public static class SettingsTests
    {
        private static string _folder = "";

        private static UserSettings Read(string json)
        {
            var path = Path.Combine(_folder, "s.json");
            File.WriteAllText(path, json);
            return SettingsStore.Load(path).Current;
        }

        // What the app relies on, whatever the file held
        private static void Sound(UserSettings? read)
        {
            var s = read!;
            True(s.General != null && s.Window != null && s.Appearance != null && s.Tables != null && s.Editing != null && s.Preview != null &&
                 s.Templates != null && s.Layout != null && s.Session != null, "sections");
            True(s.Appearance.UiScale is >= 0.8 and <= 1.5, "scale");
            True(s.Tables.FontSize is >= 10 and <= 16, "font");
            True(s.Tables.MinColumnWidth is >= 30 and <= 200 && s.Tables.MaxColumnWidth is >= 120 and <= 800, "widths");
            True(s.Tables.FixedColumnWidth is >= 60 and <= 500 && s.Tables.DetailsFieldMaxShare is >= 0.2 and <= 0.7, "fixed");
            True(s.Editing.UndoSteps is >= 50 and <= 500, "undo");
            True(Enum.IsDefined(s.Appearance.Theme) && Enum.IsDefined(s.Tables.Density) && Enum.IsDefined(s.Tables.GridLines) && Enum.IsDefined(s.Tables.ColumnSizing) &&
                 Enum.IsDefined(s.Preview.ModelDetail) && Enum.IsDefined(s.Templates.NewTableStart), "enums");
            True(s.Tables.ColumnWidths.Values.All(w => w != null && w.Values.All(v => v > 0)), "column widths");
            True(s.Tables.SearchColumns.Values.All(c => c != null && c.All(n => n != null)), "search columns");
            True(s.Layout.ActivePreset is >= 0 and <= 3 && s.Layout.Presets.Values.All(p => p != null), "layout");
            True(s.Session.OpenTables != null && s.Session.PinnedTables != null, "session");
            SettingsStore.Clone(s);
        }

        public static void RunFiles()
        {
            Section("Settings files");
            _folder = Path.Combine(Path.GetTempPath(), "alm-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            foreach (var (name, json) in new[]
                     {
                         ("empty object", "{}"), ("not JSON", "{{{"), ("an array", "[1,2]"), ("nothing", ""), ("null", "null"), ("a number", "5"), ("wrong version type", "{\"Version\":\"x\"}"),
                         ("sections null", "{\"Version\":4,\"General\":null,\"Window\":null,\"Appearance\":null,\"Tables\":null,\"Editing\":null,\"Preview\":null,\"Templates\":null,\"Layout\":null,\"Session\":null}"),
                         ("scale zero", "{\"Version\":4,\"Appearance\":{\"UiScale\":0}}"), ("scale huge", "{\"Version\":4,\"Appearance\":{\"UiScale\":1e300}}"),
                         ("unknown enum number", "{\"Version\":4,\"Appearance\":{\"Theme\":99},\"Tables\":{\"Density\":-1,\"GridLines\":7,\"ColumnSizing\":9},\"Preview\":{\"ModelDetail\":5},\"Templates\":{\"NewTableStart\":8}}"),
                         ("unknown enum name", "{\"Version\":4,\"Appearance\":{\"Theme\":\"Purple\"}}"),
                         ("widths out of range", "{\"Version\":4,\"Tables\":{\"MinColumnWidth\":-5,\"MaxColumnWidth\":0,\"FixedColumnWidth\":1e9,\"DetailsFieldMaxShare\":9,\"FontSize\":1000,\"DetailsFieldWidth\":-3}}"),
                         ("null dictionaries", "{\"Version\":4,\"Tables\":{\"ColumnWidths\":null,\"SearchColumns\":null},\"Layout\":{\"Presets\":null},\"Session\":{\"OpenTables\":null,\"PinnedTables\":null}}"),
                         ("null entries", "{\"Version\":4,\"Tables\":{\"ColumnWidths\":{\"T\":null,\"U\":{\"a\":-1,\"b\":50}},\"SearchColumns\":{\"T\":null,\"U\":[null,\"a\"]}},\"Layout\":{\"Presets\":{\"Row\":null}},\"Session\":{\"OpenTables\":[null,\"a\"]}}"),
                         ("undo steps", "{\"Version\":4,\"Editing\":{\"UndoSteps\":-1}}"), ("wrong type of value", "{\"Version\":4,\"Editing\":{\"UndoSteps\":\"many\"}}"),
                         ("layout out of range", "{\"Version\":4,\"Layout\":{\"ActivePreset\":77,\"DatabasePanelWidth\":-9,\"Presets\":{\"Row\":{\"RightWidth\":-1}}}}"),
                         ("window far away", "{\"Version\":4,\"Window\":{\"Left\":1e300,\"Top\":0,\"Width\":-5,\"Height\":10}}"),
                         ("an old version", "{\"LastDatabase\":\"x\",\"Layout\":1}"), ("unknown fields", "{\"Version\":4,\"Future\":{\"a\":1},\"Tables\":{\"Thing\":true}}"),
                         ("the same key twice", "{\"Version\":4,\"Version\":4,\"Tables\":{}}"), ("a negative version", "{\"Version\":-3}"), ("a future version", "{\"Version\":99,\"Tables\":{\"FontSize\":12}}"),
                         ("a fractional version", "{\"Version\":4.5}"), ("a null version", "{\"Version\":null}"), ("named numbers", "{\"Version\":4,\"Appearance\":{\"UiScale\":\"NaN\"},\"Tables\":{\"FontSize\":\"Infinity\"},\"Window\":{\"Left\":\"NaN\"}}"),
                         ("very deep", new string('[', 2000)), ("a byte order mark", "\uFEFF{\"Version\":4}"), ("bad unicode", "{\"Version\":4,\"Session\":{\"LastDatabase\":\"\\uD800\"}}"),
                         ("impossible paths", "{\"Version\":4,\"Session\":{\"LastDatabase\":\"C:\\\\<>|\"},\"Templates\":{\"Folder\":\"C:\\\\<>|\",\"LastExchangeFolder\":\"\\u0000\"}}")
                     })
                Test($"a file with {name}", () => Sound(Read(json)));
            Test("good values are kept", () =>
            {
                var s = Read("{\"Version\":4,\"Appearance\":{\"UiScale\":1.25,\"Theme\":\"Dark\"},\"Tables\":{\"FontSize\":15,\"MinColumnWidth\":60,\"HighlightCurrentRow\":false},\"Editing\":{\"ConfirmDelete\":true,\"EditOnOpen\":true,\"UndoSteps\":100}}");
                Equal(1.25, s.Appearance.UiScale, "scale");
                Equal(AppTheme.Dark, s.Appearance.Theme, "theme");
                Equal(15d, s.Tables.FontSize, "font");
                Equal(60d, s.Tables.MinColumnWidth, "width");
                Equal(false, s.Tables.HighlightCurrentRow, "highlight");
                Equal(100, s.Editing.UndoSteps, "undo");
                True(s.Editing.ConfirmDelete && s.Editing.EditOnOpen, "editing");
            });
            Test("a file of the version before keeps its layout and gets the new defaults", () =>
            {
                var s = Read("{\"Version\":4,\"Tables\":{\"AlternatingRows\":true},\"Layout\":{\"ActivePreset\":2}}");
                True(s.Tables.AlternatingRows, "kept");
                Equal(2, s.Layout.ActivePreset, "layout");
                Equal(13d, s.Tables.FontSize, "font");
                True(s.Tables.HighlightCurrentRow, "highlight");
                Equal(500, s.Editing.UndoSteps, "undo");
                True(!s.Editing.ConfirmDelete && !s.Editing.EditOnOpen, "nothing changes by itself");
            });
            Test("saving and reading gives the same options", () =>
            {
                var path = Path.Combine(_folder, "r.json");
                var store = SettingsStore.Load(path);
                store.Current.Tables.FontSize = 11;
                store.Current.Editing.UndoSteps = 150;
                store.Current.Tables.HighlightCurrentRow = false;
                store.Save();
                var back = SettingsStore.Load(path).Current;
                Equal(11d, back.Tables.FontSize, "font");
                Equal(150, back.Editing.UndoSteps, "undo");
                Equal(false, back.Tables.HighlightCurrentRow, "highlight");
            });
            Test("a value JSON can't hold stops neither saving nor the Options copy, and is repaired when read", () =>
            {
                var path = Path.Combine(_folder, "n.json");
                var store = SettingsStore.Load(path);
                store.Current.Layout.DatabasePanelWidth = double.NaN;
                store.Current.Tables.MaxColumnWidth = double.PositiveInfinity;
                store.Current.Window.Left = double.NaN;
                SettingsStore.Clone(store.Current);
                store.Save();
                var back = SettingsStore.Load(path).Current;
                Sound(back);
                Equal(260d, back.Layout.DatabasePanelWidth, "panel");
                Equal(null, back.Window.Left, "window");
            });
            foreach (var folder in new[] { @"C:\<>|", "Z:\\no\\such\\folder", new string('x', 5000), "\0", @"\\no-such-server\share", Environment.SystemDirectory + @"\notepad.exe" })
                Test($"a templates folder that can't work: {(folder.Length > 30 ? folder[..30] + "…" : folder.Replace("\0", "\\0"))}", () =>
                {
                    var store = new Services.Templates.TemplateStore(folder);
                    store.Load();
                });
            Test("a default that isn't a number stays out", () =>
            {
                Equal(null, Services.AccessDatabase.ParseDefault("NaN", typeof(double)), "NaN");
                Equal(null, Services.AccessDatabase.ParseDefault("Infinity", typeof(double)), "infinity");
                Equal(null, Services.AccessDatabase.ParseDefault("1e999", typeof(double)), "overflow");
            });
        }

        public static void Run()
        {
            Section("Options");
            using var test = new TestDatabase();
            test.CreateKeyedTable("T", 40);
            using var host = new TabHost(test.Database, "T");
            var tab = host.Tab;
            var grid = host.Grid;
            var (tables, editing) = (new TablesSettings(), new EditingSettings());
            var (oldTables, oldEditing) = (TableTab.Columns, TableTab.Editing);
            TableTab.Columns = tables;
            TableTab.Editing = editing;
            try
            {
                Test("the defaults look as before", () =>
                {
                    tab.ApplyGridSettings();
                    Equal(13d, grid.FontSize, "font");
                    Equal(20d, grid.RowHeight, "row");
                });
                Test("a large text size makes the rows taller, not the text cut off", () =>
                {
                    tables.FontSize = 16;
                    tab.ApplyGridSettings();
                    Wait(5);
                    Equal(16d, grid.FontSize, "font");
                    True(grid.RowHeight >= 23, $"row {grid.RowHeight}");
                    var narrow = host.Column("Description").ActualWidth;
                    tables.FontSize = 10;
                    tab.ApplyGridSettings();
                    Wait(5);
                    True(host.Column("Description").ActualWidth < narrow, "columns fit the new size");
                    tables.FontSize = 13;
                    tab.ApplyGridSettings();
                });
                Test("the selected row's shade can be turned off, and back on at once", () =>
                {
                    tab.SelectItem(host.Items[2], host.Column("Value"));
                    Equal(tab.SelectedRow, grid.Tag, "on");
                    tables.HighlightCurrentRow = false;
                    tab.ApplyGridSettings();
                    Equal(null, grid.Tag, "off");
                    Equal(host.Items[2].Row, tab.SelectedRow?.Row, "still selected");
                    tables.HighlightCurrentRow = true;
                    tab.ApplyGridSettings();
                    Equal(tab.SelectedRow, grid.Tag, "on again");
                });
                Test("deleting asks when told to", () =>
                {
                    editing.ConfirmDelete = true;
                    var count = host.Items.Count;
                    host.Select("Value", host.Items[1]);
                    Dialogs.Clear();
                    tab.DeleteSelectedRows();
                    Wait(5);
                    Equal(1, Dialogs.Count, "asked");
                    Equal(count, host.Items.Count, "Cancel keeps the row");
                    editing.ConfirmDelete = false;
                    tab.DeleteSelectedRows();
                    Wait(5);
                    Equal(count - 1, host.Items.Count, "without asking");
                    Equal(1, Dialogs.Count, "not asked");
                    tab.Undo();
                    Dialogs.Clear();
                });
                Test("undo keeps as many steps as set, the latest ones", () =>
                {
                    editing.UndoSteps = 50;
                    for (var i = 0; i < 80; i++)
                        Equal(null, tab.ChangeField(host.Items[0].Row, "Value", $"v{i}"), "change");
                    var steps = 0;
                    for (; tab.UndoDescription != null; steps++)
                        tab.Undo();
                    Equal(50, steps, "steps");
                    Equal("v29", host.Items[0]["Value"], "as it was 50 changes ago");
                    editing.UndoSteps = 500;
                    tab.Discard();
                });
                Test("lowering the undo steps with a long history keeps it working", () =>
                {
                    for (var i = 0; i < 60; i++)
                        tab.ChangeField(host.Items[0].Row, "Value", $"w{i}");
                    editing.UndoSteps = 50;
                    tab.ChangeField(host.Items[0].Row, "Value", "last");
                    tab.Undo();
                    Equal("w59", host.Items[0]["Value"], "undone");
                    editing.UndoSteps = 500;
                    tab.Discard();
                });
                Test("every combination of the look of tables, with work in between", () =>
                {
                    foreach (var height in new double[] { 20, 24, 28 })
                    foreach (var lines in Enum.GetValues<DataGridGridLinesVisibility>())
                    foreach (var font in new double[] { 10, 13, 16 })
                    foreach (var flag in new[] { false, true })
                    {
                        TableTab.RowHeight = height;
                        TableTab.GridLines = lines;
                        TableTab.AlternatingRows = flag;
                        TableTab.FreezeFirstColumn = !flag;
                        tables.FontSize = font;
                        tables.HighlightCurrentRow = flag;
                        tables.ColumnSizing = flag ? ColumnSizing.Fixed : ColumnSizing.FitContent;
                        tab.ApplyGridSettings();
                        host.Select("Description", host.Items[1], host.Items[2]);
                        Call(tab, "SetSelectedCells", "Fill", "x" + font);
                        Call(grid, "PerformSort", host.Column("Temp, Max"));
                        Wait(1);
                        tab.Undo();
                    }
                    True(!tab.IsDirty, "dirty: " + Why(tab));
                    grid.Items.SortDescriptions.Clear();
                    foreach (var column in grid.Columns)
                        column.SortDirection = null;
                    TableTab.RowHeight = 20;
                    TableTab.GridLines = DataGridGridLinesVisibility.All;
                    TableTab.AlternatingRows = false;
                    TableTab.FreezeFirstColumn = true;
                    (tables.FontSize, tables.HighlightCurrentRow, tables.ColumnSizing) = (13, true, ColumnSizing.FitContent);
                    tab.ApplyGridSettings();
                });

                OptionsWindow Open(UserSettings settings, Action<UserSettings> apply)
                {
                    var dialog = new OptionsWindow(settings, apply) { ShowActivated = false, Left = -3000, WindowStartupLocation = WindowStartupLocation.Manual };
                    dialog.Show();
                    Wait(5);
                    return dialog;
                }
                foreach (var font in new double[] { 10, 13, 16 })
                    Test($"at text size {font} the text being typed fits its row", () =>
                    {
                        tables.FontSize = font;
                        tab.ApplyGridSettings();
                        var item = host.Items[1];
                        var column = host.Column("Description");
                        tab.SelectItem(item, column);
                        grid.ScrollIntoView(item, column);
                        Wait(5);
                        grid.CurrentCell = new DataGridCellInfo(item, column);
                        grid.BeginEdit();
                        Wait(10);
                        var box = (TextBox)column.GetCellContent(item);
                        Equal(font, box.FontSize, "font");
                        True(box.ActualHeight <= grid.RowHeight + 0.5, $"box {box.ActualHeight} in a row of {grid.RowHeight}");
                        True(box.ExtentHeight <= box.ViewportHeight + 1, $"text {box.ExtentHeight} in {box.ViewportHeight}");
                        grid.CancelEdit(DataGridEditingUnit.Row);
                        tables.FontSize = 13;
                        tab.ApplyGridSettings();
                        Wait(5);
                    });
                Test("options applied in the middle of typing a row keep the row", () =>
                {
                    var item = host.Items[1];
                    var column = host.Column("Description");
                    tab.SelectItem(item, column);
                    grid.ScrollIntoView(item, column);
                    Wait(5);
                    grid.CurrentCell = new DataGridCellInfo(item, column);
                    grid.BeginEdit();
                    Wait(10);
                    ((TextBox)column.GetCellContent(item)).Text = "typing";
                    tables.FontSize = 15;
                    tables.ColumnSizing = ColumnSizing.Fixed;
                    tables.HighlightCurrentRow = false;
                    TableTab.AlternatingRows = true;
                    tab.ApplyGridSettings();
                    Wait(10);
                    grid.CommitEdit(DataGridEditingUnit.Cell, true);
                    grid.CommitEdit(DataGridEditingUnit.Row, true);
                    Wait(10);
                    Equal("typing", item["Description"], "value");
                    True(tab.IsDirty, "kept");
                    tab.Discard();
                    tables.FontSize = 13;
                    tables.ColumnSizing = ColumnSizing.FitContent;
                    tables.HighlightCurrentRow = true;
                    TableTab.AlternatingRows = false;
                    tab.ApplyGridSettings();
                    Wait(5);
                });
                Test("a new row being added survives options too", () =>
                {
                    tab.AddRow(duplicate: false);
                    Wait(5);
                    tables.FontSize = 16;
                    tab.ApplyGridSettings();
                    Wait(5);
                    tables.FontSize = 13;
                    tab.ApplyGridSettings();
                    grid.CancelEdit(DataGridEditingUnit.Row);
                    Wait(5);
                    True(!tab.IsDirty, "dirty: " + Why(tab));
                    tab.Discard();
                });
                Test("Edit turns on when a temporary tab is kept, if set; only then", () =>
                {
                    tab.IsEditing = false;
                    tab.IsPreview = true;
                    tab.IsPreview = false;
                    True(!tab.IsEditing, "off by default");
                    editing.EditOnOpen = true;
                    tab.IsPreview = true;
                    True(!tab.IsEditing, "a temporary tab stays read-only");
                    tab.IsPreview = false;
                    True(tab.IsEditing, "kept: on");
                    editing.EditOnOpen = false;
                });
                Test("awkward column names: all shown, sorted or at least safe", () =>
                {
                    using var other = new TestDatabase();
                    other.CreateKeylessTable("X");
                    using var plain = new TabHost(other.Database, "X");
                    var odd = new DataTable("X");
                    var names = new[] { "Part Number", @"x\y, z", "q'r, s", "#n, m", "^up, 5", "path/with.dot, and", "(paren), x", "{brace}, y", "\"quote\", z", "tab\there, t", "100%, *", "a/b", "c.d", "h^i" };
                    foreach (var name in names)
                        odd.Columns.Add(name, typeof(string));
                    for (var i = 0; i < 5; i++)
                        odd.Rows.Add(names.Select((n, c) => (object)$"{(char)('e' - i)}{c}").ToArray());
                    odd.AcceptChanges();
                    plain.Tab.SetTable(odd, null, other.Database);
                    Wait(10);
                    Equal(names.Length, plain.Grid.Columns.Count, "columns");
                    foreach (var name in names)
                    {
                        var column = plain.Column(name);
                        var item = plain.Items[0];
                        plain.Grid.ScrollIntoView(item, column);
                        Wait(3);
                        if (column.GetCellContent(item) is TextBlock block)
                            Equal(Text(item, name), block.Text, $"[{name}] shows");
                        if (column.CanUserSort)
                        {
                            Call(plain.Grid, "PerformSort", column);
                            Wait(3);
                            Equal(names.Select((n, c) => n == name ? c : -1).Max().ToString(), Text(plain.Items[0], name)[1..], $"[{name}] sorted");
                            True(Text(plain.Items[0], name)[0] == 'a', $"[{name}] sorted up");
                        }
                    }
                    True(plain.Column("#n, m").CanUserSort, "a mark and a comma sort");
                    plain.Grid.Items.SortDescriptions.Clear();
                });

                Binding? BindingOf(DependencyObject control, DependencyProperty property) => BindingOperations.GetBinding(control, property);
                Test("each new control writes its option", () =>
                {
                    UserSettings? applied = null;
                    var dialog = Open(new UserSettings(), s => applied = s);
                    var pages = (ListBox)dialog.FindName("Categories");
                    pages.SelectedIndex = 2;
                    Wait(10);
                    pages.SelectedIndex = 3;
                    Wait(10);
                    var checks = All<CheckBox>(dialog).Concat(All<CheckBox>((DependencyObject)dialog.FindName("TablesPage"))).ToList();
                    foreach (var check in checks.Where(c => BindingOf(c, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.Path.Path is "Editing.EditOnOpen" or "Editing.ConfirmDelete"))
                        check.IsChecked = true;
                    var sliders = All<Slider>(dialog).ToList();
                    sliders.First(s => BindingOf(s, System.Windows.Controls.Primitives.RangeBase.ValueProperty)?.Path.Path == "Editing.UndoSteps").Value = 150;
                    pages.SelectedIndex = 2;
                    Wait(10);
                    All<Slider>(dialog).First(s => BindingOf(s, System.Windows.Controls.Primitives.RangeBase.ValueProperty)?.Path.Path == "Tables.FontSize").Value = 15;
                    All<CheckBox>(dialog).First(c => BindingOf(c, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.Path.Path == "Tables.HighlightCurrentRow").IsChecked = false;
                    Call(dialog, "Apply_Click", null, null);
                    dialog.Close();
                    True(applied!.Editing.EditOnOpen && applied.Editing.ConfirmDelete, "checks");
                    Equal(150, applied.Editing.UndoSteps, "undo");
                    Equal(15d, applied.Tables.FontSize, "font");
                    Equal(false, applied.Tables.HighlightCurrentRow, "highlight");
                });
                Test("a click on a slider's bar goes there, on a step; Page Up moves one step", () =>
                {
                    var dialog = Open(new UserSettings(), _ => { });
                    var pages = (ListBox)dialog.FindName("Categories");
                    var seen = 0;
                    for (var i = 0; i < pages.Items.Count; i++)
                    {
                        pages.SelectedIndex = i;
                        Wait(10);
                        foreach (var slider in All<Slider>(dialog).Where(s => s.IsVisible))
                        {
                            seen++;
                            var name = BindingOperations.GetBinding(slider, System.Windows.Controls.Primitives.RangeBase.ValueProperty)?.Path.Path;
                            True(slider.IsMoveToPointEnabled, $"{name}: a click goes to the point");
                            Equal(slider.TickFrequency, slider.LargeChange, $"{name}: step");
                            var track = (System.Windows.Controls.Primitives.Track)slider.Template.FindName("PART_Track", slider);
                            True(track is { Thumb: not null }, $"{name}: the theme has the parts a click needs");
                            foreach (var share in new[] { 0.25, 0.5, 0.75 })
                            {
                                // What Slider does with a click on the bar, at this share of its length
                                var wanted = track.ValueFromPoint(new Point(track.ActualWidth * share, track.ActualHeight / 2));
                                Call(slider, "UpdateValue", wanted);
                                Wait(1);
                                var expected = slider.Minimum + (slider.Maximum - slider.Minimum) * share;
                                True(Math.Abs(slider.Value - expected) <= slider.TickFrequency + (slider.Maximum - slider.Minimum) * 0.05,
                                    $"{name}: {share:P0} of the bar gave {slider.Value}, expected about {expected}");
                                var steps = (slider.Value - slider.Minimum) / slider.TickFrequency;
                                True(Math.Abs(steps - Math.Round(steps)) < 1e-6, $"{name}: {slider.Value} is on a step");
                            }
                            var before = slider.Value;
                            Slider.IncreaseLarge.Execute(null, slider);
                            Wait(1);
                            True(Math.Abs(slider.Value - before - slider.TickFrequency) < 1e-6, $"{name}: one step up gave {slider.Value} from {before}");
                        }
                    }
                    True(seen >= 6, $"{seen} sliders");
                    dialog.Close();
                });
                Test("values between the slider steps stay as they are", () =>
                {
                    var settings = new UserSettings();
                    settings.Editing.UndoSteps = 73;
                    settings.Tables.FontSize = 12.5;
                    settings.Appearance.UiScale = 1.03;
                    settings.Tables.MinColumnWidth = 42;
                    UserSettings? applied = null;
                    var dialog = Open(settings, s => applied = s);
                    var pages = (ListBox)dialog.FindName("Categories");
                    for (var i = 0; i < pages.Items.Count; i++)
                    {
                        pages.SelectedIndex = i;
                        Wait(5);
                    }
                    Call(dialog, "Apply_Click", null, null);
                    dialog.Close();
                    Equal(73, applied!.Editing.UndoSteps, "undo");
                    Equal(12.5, applied.Tables.FontSize, "font");
                    Equal(1.03, applied.Appearance.UiScale, "scale");
                    Equal(42d, applied.Tables.MinColumnWidth, "width");
                });
                Test("the Options window opens with an impossible templates folder", () =>
                {
                    var settings = new UserSettings();
                    settings.Templates.Folder = @"C:\<>|";
                    settings.Templates.LastExchangeFolder = "\0";
                    var dialog = Open(settings, _ => { });
                    dialog.Close();
                });
                Test("the Options window opens on every page, with settings read from a bad file", () =>
                {
                    var settings = Read("{\"Version\":4,\"Appearance\":{\"UiScale\":-1,\"Theme\":77},\"Tables\":null,\"Editing\":{\"UndoSteps\":0}}");
                    UserSettings? applied = null;
                    var dialog = Open(settings, s => applied = s);
                    var pages = (ListBox)dialog.FindName("Categories");
                    Equal(6, pages.Items.Count, "pages");
                    for (var i = 0; i < pages.Items.Count; i++)
                    {
                        pages.SelectedIndex = i;
                        Wait(5);
                    }
                    Call(dialog, "Apply_Click", null, null);
                    True(applied != null, "applied");
                    Sound(applied!);
                    dialog.Close();
                });
                Test("Options changes nothing by being opened and applied", () =>
                {
                    var settings = new UserSettings();
                    settings.Tables.ColumnWidths["T"] = new() { ["a"] = 50 };
                    settings.Editing.UndoSteps = 200;
                    settings.Tables.FontSize = 12;
                    var before = System.Text.Json.JsonSerializer.Serialize(settings);
                    UserSettings? applied = null;
                    var dialog = Open(settings, s => applied = s);
                    var pages = (ListBox)dialog.FindName("Categories");
                    for (var i = 0; i < pages.Items.Count; i++)
                    {
                        pages.SelectedIndex = i;
                        Wait(5);
                    }
                    Call(dialog, "Apply_Click", null, null);
                    dialog.Close();
                    Equal(before, System.Text.Json.JsonSerializer.Serialize(applied), "settings");
                });
                Test("Restore defaults: options go back, widths and search columns stay", () =>
                {
                    var settings = new UserSettings();
                    settings.Tables.ColumnWidths["T"] = new() { ["a"] = 50 };
                    settings.Tables.SearchColumns["T"] = ["a"];
                    settings.Tables.FontSize = 15;
                    settings.Tables.AlternatingRows = true;
                    settings.Tables.Density = TableDensity.Comfortable;
                    settings.Tables.ColumnSizing = ColumnSizing.Fixed;
                    settings.Tables.GridLines = GridLines.None;
                    settings.Editing.ConfirmDelete = true;
                    settings.Editing.UndoSteps = 100;
                    settings.Appearance.UiScale = 1.3;
                    settings.Appearance.Theme = AppTheme.Dark;
                    settings.Preview.ModelDetail = ModelDetail.High;
                    settings.Templates.Folder = @"C:\kept";
                    var fresh = new UserSettings();
                    UserSettings? applied = null;
                    var dialog = Open(settings, s => applied = s);
                    var saved = AppDialog.Instead;
                    AppDialog.Instead = (_, _, _, _) => 0;
                    Call(dialog, "RestoreDefaults_Click", null, null);
                    AppDialog.Instead = saved;
                    Wait(5);
                    Call(dialog, "Apply_Click", null, null);
                    dialog.Close();
                    var a = applied!;
                    Equal(fresh.Tables.FontSize, a.Tables.FontSize, "font");
                    Equal(false, a.Tables.AlternatingRows, "shade");
                    Equal(fresh.Tables.Density, a.Tables.Density, "density");
                    Equal(fresh.Tables.ColumnSizing, a.Tables.ColumnSizing, "sizing");
                    Equal(fresh.Tables.GridLines, a.Tables.GridLines, "lines");
                    Equal(false, a.Editing.ConfirmDelete, "confirm");
                    Equal(500, a.Editing.UndoSteps, "undo");
                    Equal(1d, a.Appearance.UiScale, "scale");
                    Equal(fresh.Appearance.Theme, a.Appearance.Theme, "theme");
                    Equal(fresh.Preview.ModelDetail, a.Preview.ModelDetail, "detail");
                    Equal(50d, a.Tables.ColumnWidths["T"]["a"], "widths stay");
                    Equal("a", a.Tables.SearchColumns["T"][0], "search stays");
                    Equal(@"C:\kept", a.Templates.Folder, "templates stay");
                });
            }
            finally
            {
                TableTab.Columns = oldTables;
                TableTab.Editing = oldEditing;
            }
        }
    }
}
