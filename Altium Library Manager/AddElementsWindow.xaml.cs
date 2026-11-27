using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Altium_Library_Manager.Services;
using Microsoft.Win32;

namespace Altium_Library_Manager
{
    /// <summary>A field of the rows being made: its text, which may hold placeholders (see <see cref="ElementValues"/>).</summary>
    public sealed class ElementField(string name, string pattern, FieldLink link) : INotifyPropertyChanged
    {
        private string _pattern = pattern;

        public string Name { get; } = name;

        public FieldLink Link { get; } = link;

        public string Pattern
        {
            get => _pattern;
            set
            {
                if (_pattern == value)
                    return;
                _pattern = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Pattern)));
            }
        }

        private bool _update;
        private bool _showUpdate;

        /// <summary>When updating existing rows: this field is written to them.</summary>
        public bool Update
        {
            get => _update;
            set
            {
                if (_update == value)
                    return;
                _update = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Update)));
            }
        }

        /// <summary>Existing rows are being updated, so the field's tick is shown.</summary>
        public bool ShowUpdate
        {
            get => _showUpdate;
            set
            {
                if (_showUpdate == value)
                    return;
                _showUpdate = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowUpdate)));
            }
        }

        public bool HasButton => Link != FieldLink.None;

        public string Glyph => FieldLinks.IsRef(Link) ? "" : "";

        public string Tip => FieldLinks.IsRef(Link) ? "Choose from the library in the path field" : "Choose a file";

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>One row of the preview.</summary>
    public sealed record ElementRow(string PartNumber, string Value, string Description, string Note);

    /// <summary>
    /// Adds many similar components at once: the same fields, differing in their value. The values come from a standard
    /// series or a typed list; the fields start from the selected row, its value turned into a placeholder, so any table
    /// works: what a row gets is just text, shown in the preview before it is added. The rows join the table as one
    /// step of its history, and reach the database when it is saved.
    /// </summary>
    public partial class AddElementsWindow : Window
    {
        private readonly TableTab _tab;
        private readonly AccessDatabase _database;
        private readonly AltiumRenderService _service;
        private readonly HashSet<string> _partNumbers;
        private readonly List<ElementField> _fields = [];
        private readonly string? _partNumberField;
        private List<Dictionary<string, string>> _rows = [];
        private string? _valueField;
        private string? _descriptionField;
        private bool _loading = true;

        public AddElementsWindow(TableTab tab, AccessDatabase database, AltiumRenderService service)
        {
            InitializeComponent();
            _tab = tab;
            _database = database;
            _service = service;
            _partNumbers = tab.PartNumbers();
            _partNumberField = tab.PartNumberField?.ColumnName;
            // The cells of the app's tables: Fluent's own shift their text and frame the current one
            PreviewGrid.CellStyle = TableTab.GridCellStyle;
            PreviewGrid.ColumnHeaderStyle = TableTab.GridColumnHeaderStyle;

            SeriesCombo.ItemsSource = ElementValues.AllSeries;
            var problems = new List<string>();
            var types = ElementTypes.Load(problems);
            KindCombo.ItemsSource = types;
            // Rows are found by their Part Number: without one, there is nothing to update by
            UpdateModeRadio.IsEnabled = _partNumberField != null;
            var choice = App.Settings.Current.Tables.AddElements.GetValueOrDefault(tab.Name);
            if (choice != null)
            {
                _ticked.UnionWith(choice.UpdateFields?.OfType<string>() ?? []);
                UpdateModeRadio.IsChecked = choice.Update && _partNumberField != null;
            }
            KindCombo.SelectedItem = types.FirstOrDefault(t => t.Name == choice?.Type) ?? ElementTypes.ForTable(types, tab.Name) ??
                                     types.FirstOrDefault(t => t.Name == "Other") ?? types[0];
            LoadType();
            if (choice != null && choice.Type == Type.Name)
                Restore(choice);
            _loading = false;
            Closed += (_, _) => Remember();
            UpdateSource();
            Update();
            Loaded += (_, _) =>
            {
                FitToFields();
                (SeriesRadio.IsChecked == true ? FromBox : ValuesList).Focus();
                if (problems.Count > 0)
                    AppDialog.Warning(this, "Add elements", "Some element types couldn't be read", string.Join("\n", problems));
            };
        }

        private ElementType Type => (ElementType)KindCombo.SelectedItem;

        /// <summary>Opens as it was last left for this table (see <see cref="Remember"/>); a series that no longer
        /// exists keeps the type's own.</summary>
        private void Restore(Services.Settings.AddElementsChoice choice)
        {
            var numbers = Type.Style != ElementStyle.Text;
            if (numbers && ElementValues.AllSeries.FirstOrDefault(s => s.Name == choice.Series) is { } series)
            {
                SeriesCombo.SelectedItem = series;
                _shownSeries = series.Name;
                WithE24Check.IsChecked = choice.WithE24;
                UpdateE24Check();
            }
            if (choice.From != null)
                FromBox.Text = choice.From;
            if (choice.To != null)
                ToBox.Text = choice.To;
            if (choice.Values != null)
                ValuesList.Text = choice.Values;
            SeriesRadio.IsChecked = numbers && !choice.List;
            ListRadio.IsChecked = !numbers || choice.List;
            JumperCheck.IsChecked = choice.Jumper;
        }

        /// <summary>Keeps how the window was left, by table: the type, the values and the ticks (not the fields,
        /// which come from the table's rows each time; a type of its own keeps those, see Save as).</summary>
        private void Remember()
        {
            if (KindCombo.SelectedItem is not ElementType type)
                return;
            App.Settings.Current.Tables.AddElements[_tab.Name] = new Services.Settings.AddElementsChoice
            {
                Type = type.Name,
                List = ListRadio.IsChecked == true,
                Series = (SeriesCombo.SelectedItem as SeriesInfo)?.Name,
                WithE24 = WithE24Check.IsChecked == true,
                From = FromBox.Text,
                To = ToBox.Text,
                Values = ValuesList.Text.Length <= Services.Settings.AddElementsChoice.MaxValuesLength ? ValuesList.Text : null,
                Jumper = JumperCheck.IsChecked == true,
                Update = Updating,
                UpdateFields = [.. _ticked]
            };
            App.Settings.RequestSave();
        }

        // Wide enough for a Ref and a Path side by side
        private const double FittedWidth = 1320;

        /// <summary>
        /// Opens as large as the fields need, so all of them show without resizing the window: taller by what the
        /// field list can't show, wider for the rows of two fields, never past the screen, still centered on the
        /// main window. A table with more fields than fit gets the whole screen height, and the list scrolls.
        /// </summary>
        private void FitToFields()
        {
            if (ScreenFit.WorkArea(this) is not { } area)
                return;
            UpdateLayout();
            var missing = Math.Max(FieldScroller.ExtentHeight - FieldScroller.ViewportHeight, 0);
            var height = Math.Min(missing > 0 ? ActualHeight + missing + 4 : ActualHeight, area.Height);
            var width = Math.Min(Math.Max(ActualWidth, FittedWidth), area.Width);
            if (height <= ActualHeight && width <= ActualWidth)
                return;
            var (grownX, grownY) = ((width - ActualWidth) / 2, (height - ActualHeight) / 2);
            Width = width;
            Height = height;
            if (Owner != null)
            {
                Left = Math.Clamp(Left - grownX, area.Left, area.Right - width);
                Top = Math.Clamp(Top - grownY, area.Top, area.Bottom - height);
            }
        }

        /// <summary>Starts from what the type offers: its series or list, and its fields if it is a saved set.</summary>
        private void LoadType()
        {
            var type = Type;
            var loading = _loading;
            _loading = true;
            var series = type.Series.EndsWith("+E24", StringComparison.Ordinal) ? type.Series[..^4] : type.Series;
            SeriesCombo.SelectedItem = ElementValues.AllSeries.FirstOrDefault(s => s.Name == series) ?? ElementValues.AllSeries[3];
            WithE24Check.IsChecked = type.Series.EndsWith("+E24", StringComparison.Ordinal);
            UpdateE24Check();
            // Resistors only; hidden, not collapsed, for the others, so nothing moves
            JumperCheck.Visibility = ElementValues.HasJumper(type) ? Visibility.Visible : Visibility.Hidden;
            FromBox.Text = type.From;
            ToBox.Text = type.To;
            ValuesList.Text = type.Values;
            ReadyLists.ItemsSource = type.Lists.Keys.ToList();
            ReadyLists.SelectedItem = null;
            ReadyLists.Visibility = type.Lists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var list = type.Style == ElementStyle.Text || type.Values.Length > 0;
            SeriesRadio.IsChecked = !list;
            ListRadio.IsChecked = list;
            LoadFields(type);
            _loading = loading;
        }

        /// <summary>The fields, from the selected row with its value as a placeholder; the ones with text come first.</summary>
        private void LoadFields(ElementType type)
        {
            foreach (var field in _fields)
                field.PropertyChanged -= Field_Changed;
            _fields.Clear();

            // The selected row, or else the first one shown: the fields start from a row of the table either way
            var row = _tab.SelectedRow?.Row ?? _tab.Grid.Items.OfType<DataRowView>().Select(v => v.Row)
                .FirstOrDefault(r => r.RowState is not (DataRowState.Deleted or DataRowState.Detached));
            var table = _tab.Table!;
            var valueColumn = type.ValueFields.Select(n => AccessDatabase.FindColumn(table, n)).OfType<DataColumn>().FirstOrDefault();
            _valueField = valueColumn?.ColumnName;
            _descriptionField = AccessDatabase.FindColumn(table, "Description")?.ColumnName;
            string? Text(DataColumn c) => row == null || row.IsNull(c) ? null : Convert.ToString(row[c]);

            // What the selected row's value looks like, to find it in its other fields
            var baseValue = valueColumn != null && Text(valueColumn) is { Length: > 0 } text ? ElementValues.FromText(type, text) : null;
            var fields = new List<ElementField>();
            foreach (var column in _tab.NewRowColumns())
            {
                var value = Text(column) ?? "";
                var pattern = baseValue != null && column.DataType == typeof(string) ? ElementValues.ToPattern(value, baseValue) : value;
                if (column.ColumnName == _valueField)
                    pattern = baseValue != null ? pattern : "{Value}";
                if (column.ColumnName == _partNumberField && !pattern.Contains('{'))
                    pattern = pattern.Length > 0 ? pattern + " {Value}" : "{Value}";
                // A saved set brings its own texts
                if (type.Patterns.TryGetValue(column.ColumnName, out var saved))
                    pattern = saved;
                fields.Add(new ElementField(column.ColumnName, pattern,
                    column.DataType == typeof(string) ? FieldLinks.Classify(column.ColumnName) : FieldLink.None));
            }

            // Altium's fields first, then what the row has, then the empty ones
            var leading = TableTab.LeadingColumns.Select(n => fields.FirstOrDefault(f => string.Equals(f.Name.Replace(" ", ""), n.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)))
                .OfType<ElementField>().ToList();
            _fields.AddRange(leading);
            _fields.AddRange(fields.Except(leading).OrderBy(f => f.Pattern.Length == 0));
            foreach (var field in _fields)
                field.PropertyChanged += Field_Changed;
            ShowTicks();
            FieldList.ItemsSource = ElementFieldGroups.Build(_fields, _partNumberField, _valueField);
            FieldScroller.ScrollToTop();
            var source = row != null && _partNumberField != null ? Convert.ToString(row[_partNumberField]) : null;
            FieldsTitle.Text = string.IsNullOrEmpty(source) ? "Fields" : $"Fields, from {source}";
            var sample = ElementValues.Parse(type, "4.7") is { } number ? ElementValues.Format(type, number) : new ElementValue("Red", "Red");
            PatternHint.Text = $"Each row gets these texts. In them, {{Value}} stands for {sample.Code}, {{VALUE}} for {sample.Code.ToUpperInvariant()}, " +
                               $"{{Text}} for {sample.Text}, {{Short}} for {sample.Short}, and {{N}} for the row's number.";
            PreviewValueColumn.Header = _valueField ?? "Value";
        }

        private void Kind_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || KindCombo.SelectedItem == null)
                return;
            LoadType();
            UpdateSource();
            Update();
        }

        private void Source_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;
            UpdateSource();
            Update();
        }

        private void ReadyLists_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || ReadyLists.SelectedItem is not string name || !Type.Lists.TryGetValue(name, out var list))
                return;
            ListRadio.IsChecked = true;
            ValuesList.Text = string.Join(Environment.NewLine, list);
        }

        private void UpdateSource()
        {
            var numbers = Type.Style != ElementStyle.Text;
            SeriesRadio.IsEnabled = numbers;
            if (!numbers)
                ListRadio.IsChecked = true;
            var series = SeriesRadio.IsChecked == true;
            SeriesPanel.IsEnabled = series;
            SeriesPanel.Opacity = series ? 1 : 0.5;
            ValuesList.IsEnabled = !series;
        }

        private void Inputs_Changed(object sender, RoutedEventArgs e)
        {
            UpdateE24Check();
            Update();
        }


        // The E24 values are offered only for the series without them; picking one of those turns them on, as 4.7k is what
        // people look for (a saved type keeps what it was saved with)
        private string? _shownSeries;

        private void UpdateE24Check()
        {
            var name = (SeriesCombo.SelectedItem as SeriesInfo)?.Name ?? "";
            if (!_loading && name != _shownSeries && ElementValues.CanAddE24(name))
                WithE24Check.IsChecked = true;
            _shownSeries = name;
            // Hidden, not collapsed: its place stays, so nothing moves when another series is picked
            WithE24Check.Visibility = ElementValues.CanAddE24(name) ? Visibility.Visible : Visibility.Hidden;
        }

        private bool WithJumper => JumperCheck.IsChecked == true && ElementValues.HasJumper(Type);

        private string SelectedSeries()
        {
            var name = ((SeriesInfo)SeriesCombo.SelectedItem).Name;
            return ElementValues.CanAddE24(name) && WithE24Check.IsChecked == true ? name + "+E24" : name;
        }

        private void Field_Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ElementField.ShowUpdate))
                return;
            if (!_loading && sender is ElementField field && e.PropertyName == nameof(ElementField.Update))
            {
                if (field.Update)
                    _ticked.Add(field.Name);
                else
                    _ticked.Remove(field.Name);
            }
            Update();
        }

        /// <summary>The values asked for, or why there are none.</summary>
        private (List<ElementValue> Values, string? Problem) Values()
        {
            var type = Type;
            if (SeriesRadio.IsChecked != true)
                return (ElementValues.ParseList(type, ValuesList.Text), null);
            if (ElementValues.Parse(type, FromBox.Text) is not { } from)
                return ([], "\"From\" isn't a value of this element.");
            if (ElementValues.Parse(type, ToBox.Text) is not { } to)
                return ([], "\"To\" isn't a value of this element.");
            if (from <= 0 || to < from)
                return ([], ElementValues.HasJumper(type) && from == 0
                    ? "\"From\" has to be above zero; for a 0 Ohm jumper, tick \"+ 0 Ohm jumper\"."
                    : "\"From\" has to be above zero, and not above \"To\".");
            return (ElementValues.Series(SelectedSeries(), from, to).Select(v => ElementValues.Format(type, v)).ToList(), null);
        }

        /// <summary>Works out the rows: what each one gets, which are left out (their Part Number is taken) and what is
        /// wrong, and shows them.</summary>
        private void Update()
        {
            if (_loading)
                return;
            var (values, problem) = Values();
            // The jumper comes first, as 0 is the smallest value. In a table that has the values already, they are left
            // out as they are, and only the jumper is added.
            var jumpers = WithJumper ? 1 : 0;
            if (jumpers > 0)
                values.Insert(0, ElementValues.Jumper(Type));
            ValuesHint.Text = Type.Style == ElementStyle.Text ? $"{values.Count} values." : $"A bare number means {Type.BareUnit}. {values.Count} values.";
            if (values.Count > ElementValues.MaxValues)
            {
                problem = $"That's {values.Count} values; at most {ElementValues.MaxValues} can be added at once.";
                values = [];
            }

            if (Updating)
            {
                ShowUpdates(values, jumpers, problem);
                return;
            }

            var columns = _tab.NewRowColumns().ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<Dictionary<string, string>>();
            var preview = new List<ElementRow>();
            var skipped = 0;
            for (var i = 0; i < values.Count; i++)
            {
                var (value, number) = (values[i], i + 1);
                var row = _fields.ToDictionary(f => f.Name, f => i < jumpers ? ElementValues.ApplyJumper(f.Pattern, value, number) : ElementValues.Apply(f.Pattern, value, number),
                    StringComparer.OrdinalIgnoreCase);
                var partNumber = _partNumberField != null ? row.GetValueOrDefault(_partNumberField, "").Trim() : "";
                string note;
                if (_partNumberField != null && partNumber.Length == 0)
                {
                    problem ??= "Part Number can't be empty.";
                    note = "No part number";
                }
                else if (_partNumbers.Contains(partNumber))
                {
                    note = "Already in the table";
                    skipped++;
                }
                else if (_partNumberField != null && !seen.Add(partNumber))
                {
                    note = "Same as a row above";
                    skipped++;
                }
                else
                {
                    note = "";
                    foreach (var (name, text) in row)
                        if (columns.TryGetValue(name, out var column) && _tab.NewValueProblem(column, text) is { } wrong)
                        {
                            problem ??= wrong;
                            note = "Can't be added";
                        }
                    rows.Add(row);
                }
                preview.Add(new ElementRow(partNumber, _valueField != null ? row.GetValueOrDefault(_valueField, "") : values[i].Code,
                    _descriptionField != null ? row.GetValueOrDefault(_descriptionField, "") : "", note));
            }

            _rows = rows;
            PreviewGrid.ItemsSource = preview;
            PreviewTitle.Text = skipped == 0 ? $"Rows: {rows.Count}" : $"Rows: {rows.Count} new, {skipped} left out";
            AddButton.Content = rows.Count == 1 ? "Add 1 row" : $"Add {rows.Count} rows";
            AddButton.IsEnabled = rows.Count > 0 && problem == null;
            ErrorText.Text = problem ?? "";
            ErrorText.ToolTip = problem;
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            if (Updating)
            {
                if (_tab.UpdateRows(_updates) is { } problem)
                {
                    ErrorText.Text = problem;
                    ErrorText.ToolTip = problem;
                    return;
                }
            }
            else
                _tab.AddRows(_rows);
            Close();
        }

        #region Updating existing rows

        private List<(DataRow Row, IReadOnlyDictionary<string, string> Fields)> _updates = [];
        private Dictionary<string, List<DataRow>>? _existing;
        // The ticked fields, kept while the fields are made again for another type
        private readonly HashSet<string> _ticked = new(StringComparer.OrdinalIgnoreCase);

        private bool Updating => UpdateModeRadio.IsChecked == true;

        private void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;
            ShowTicks();
            Update();
        }

        private void ShowTicks()
        {
            var loading = _loading;
            _loading = true;
            foreach (var field in _fields)
            {
                // Rows are found by their Part Number, so it isn't one of the fields to change
                field.ShowUpdate = Updating && field.Name != _partNumberField;
                field.Update = _ticked.Contains(field.Name);
            }
            _loading = loading;
        }

        /// <summary>
        /// Updating: the rows the table has with these values' Part Numbers, each with the ticked fields' texts, and
        /// what would change in it. Several rows with one Part Number all get them.
        /// </summary>
        private void ShowUpdates(List<ElementValue> values, int jumpers, string? problem)
        {
            _existing ??= _tab.RowsByPartNumber();
            var ticked = _fields.Where(f => f.Update && f.Name != _partNumberField).ToList();
            if (ticked.Count == 0)
                problem ??= "Tick the fields to change in the existing rows (the boxes next to them).";
            var columns = _tab.NewRowColumns().ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var updates = new List<(DataRow, IReadOnlyDictionary<string, string>)>();
            var preview = new List<ElementRow>();
            var (missing, same) = (0, 0);
            for (var i = 0; i < values.Count; i++)
            {
                var (value, number) = (values[i], i + 1);
                string Text(ElementField f) => i < jumpers ? ElementValues.ApplyJumper(f.Pattern, value, number) : ElementValues.Apply(f.Pattern, value, number);
                var partNumber = _partNumberField != null && _fields.FirstOrDefault(f => f.Name == _partNumberField) is { } pn ? Text(pn).Trim() : "";
                var fields = ticked.ToDictionary(f => f.Name, Text, StringComparer.OrdinalIgnoreCase);
                var shownValue = _valueField != null && _fields.FirstOrDefault(f => f.Name == _valueField) is { } v ? Text(v) : value.Code;
                if (!seen.Add(partNumber))
                {
                    preview.Add(new ElementRow(partNumber, shownValue, "", "Same as a row above"));
                    continue;
                }
                if (!_existing.TryGetValue(partNumber, out var rows))
                {
                    missing++;
                    preview.Add(new ElementRow(partNumber, shownValue, "", "Not in the table"));
                    continue;
                }
                var wrong = fields.Select(p => columns.TryGetValue(p.Key, out var c) ? _tab.NewValueProblem(c, p.Value) : null).FirstOrDefault(p => p != null);
                foreach (var row in rows)
                {
                    var description = _descriptionField == null ? ""
                        : fields.TryGetValue(_descriptionField, out var text) ? text : Convert.ToString(row[_descriptionField]) ?? "";
                    string note;
                    if (wrong != null)
                    {
                        problem ??= wrong;
                        note = "Can't be changed";
                    }
                    else if (_tab.ChangedFields(row, fields) is { Count: > 0 } changed)
                    {
                        // A ticked field left empty empties the rows' field: said apart, so it isn't missed
                        var cleared = changed.Where(name => fields[name].Trim().Length == 0).ToList();
                        var set = changed.Except(cleared).ToList();
                        note = string.Join("; ", new[]
                        {
                            set.Count > 0 ? "Changes " + string.Join(", ", set) : null,
                            cleared.Count > 0 ? "Clears " + string.Join(", ", cleared) : null
                        }.OfType<string>());
                        updates.Add((row, fields));
                    }
                    else
                    {
                        note = "Already like this";
                        same++;
                    }
                    preview.Add(new ElementRow(partNumber, shownValue, description, note));
                }
            }

            _updates = updates;
            PreviewGrid.ItemsSource = preview;
            var parts = new List<string> { $"{updates.Count} to change" };
            if (same > 0)
                parts.Add($"{same} already like this");
            if (missing > 0)
                parts.Add($"{missing} not in the table");
            PreviewTitle.Text = $"Rows: {string.Join(", ", parts)}";
            AddButton.Content = updates.Count == 1 ? "Update 1 row" : $"Update {updates.Count} rows";
            AddButton.IsEnabled = updates.Count > 0 && problem == null;
            ErrorText.Text = problem ?? "";
            ErrorText.ToolTip = problem;
        }

        #endregion

        #region Type files

        private static string? ExchangeFolder =>
            App.Settings.Current.Templates.LastExchangeFolder is { } folder && Directory.Exists(folder) ? folder : null;

        private static void RememberFolder(string file)
        {
            App.Settings.Current.Templates.LastExchangeFolder = Path.GetDirectoryName(file);
            App.Settings.RequestSave();
        }

        // After the files changed: the list again, with this type selected
        private void ReloadTypes(string? select)
        {
            var problems = new List<string>();
            var types = ElementTypes.Load(problems);
            _loading = true;
            KindCombo.ItemsSource = types;
            KindCombo.SelectedItem = types.FirstOrDefault(t => t.Name == select) ?? types[0];
            _loading = false;
            LoadType();
            UpdateSource();
            Update();
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import element types",
                Filter = "Element types (*.json)|*.json|All files|*.*",
                Multiselect = true,
                InitialDirectory = ExchangeFolder
            };
            if (dialog.ShowDialog(this) != true)
                return;
            RememberFolder(dialog.FileName);

            var problems = new List<string>();
            string? last = null;
            foreach (var file in dialog.FileNames)
            {
                try
                {
                    var type = ElementTypes.Read(file);
                    var target = ElementTypes.PathFor(type.Name);
                    if (File.Exists(target) && !AppDialog.Confirm(this, "Import element types", $"There is already a type \"{type.Name}\"", "Replace",
                            "The imported one takes its place."))
                        continue;
                    Directory.CreateDirectory(ElementTypes.Folder);
                    ElementTypes.Write(type, target);
                    last = type.Name;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
                {
                    problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
            if (last != null)
                ReloadTypes(last);
            if (problems.Count > 0)
                AppDialog.Error(this, "Import element types", "Some files couldn't be imported", string.Join("\n", problems));
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export element type",
                Filter = "Element types (*.json)|*.json",
                FileName = Path.GetFileName(ElementTypes.PathFor(Type.Name)),
                InitialDirectory = ExchangeFolder
            };
            if (dialog.ShowDialog(this) != true)
                return;
            RememberFolder(dialog.FileName);
            try
            {
                ElementTypes.Write(Type, dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppDialog.Error(this, "Export element type", "Couldn't write the file", ex.Message);
            }
        }

        /// <summary>Keeps what is set up now as a type of its own: the values and every field's text.</summary>
        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (AppDialog.Prompt(this, "Save element type", "Name of the new type", Type.Name, "Save")?.Trim() is not { Length: > 0 } name)
                return;
            var type = Type.Copy();
            type.Name = name;
            type.Series = SelectedSeries();
            type.From = FromBox.Text;
            type.To = ToBox.Text;
            type.Values = SeriesRadio.IsChecked == true ? "" : ValuesList.Text;
            type.Patterns = _fields.ToDictionary(f => f.Name, f => f.Pattern);
            // Chosen by hand: a set shouldn't take the place of the plain type for a table
            type.TableWords = [];
            type.GeneralWords = [];

            var path = ElementTypes.PathFor(name);
            if (File.Exists(path) && !AppDialog.Confirm(this, "Save element type", $"There is already a type \"{name}\"", "Replace"))
                return;
            try
            {
                Directory.CreateDirectory(ElementTypes.Folder);
                ElementTypes.Write(type, path);
                ReloadTypes(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppDialog.Error(this, "Save element type", "Couldn't write the file", ex.Message);
            }
        }

        private void Folder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(ElementTypes.Folder);
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { ElementTypes.Folder } });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                AppDialog.Error(this, "Add elements", "Couldn't open the folder", ex.Message);
            }
        }

        #endregion

        #region Paths and refs

        private void FieldButton_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            if (button.DataContext is not ElementField field || DropDown.WasJustClosed(button))
                return;
            if (FieldLinks.IsRef(field.Link))
                ShowComponents(field, button);
            else
                ChooseFile(field);
        }

        private void ChooseFile(ElementField field)
        {
            var folder = Path.GetDirectoryName(_database.FilePath)!;
            var dialog = new OpenFileDialog { Title = $"Choose a file for {field.Name}", Filter = FieldLinks.Filter(field.Link), InitialDirectory = folder };
            if (dialog.ShowDialog(this) != true)
                return;
            var example = field.Pattern.Length > 0 && FieldLinks.WebAddress(field.Pattern) == null ? field.Pattern
                : _fields.FirstOrDefault(f => f.Link is FieldLink.Symbol or FieldLink.Footprint && f.Pattern.Length > 0)?.Pattern;
            field.Pattern = FieldLinks.StoredPath(dialog.FileName, folder, example);
        }

        /// <summary>A menu of the components of the field's library (the path as it is typed here).</summary>
        private async void ShowComponents(ElementField field, Button button)
        {
            var library = FieldLinks.LibraryFields(field.Name, field.Link)
                .Select(name => _fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))?.Pattern)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            if (library == null)
            {
                ErrorText.Text = $"Fill in the path of {field.Name} first";
                return;
            }

            var wanted = field.Link == FieldLink.SymbolRef ? LibraryKind.SchLib : LibraryKind.PcbLib;
            var (database, service) = (_database, _service);
            var (names, problem) = await Task.Run(() => service.ListLibrary(database, library, wanted));
            if (names == null)
            {
                AppDialog.Error(this, "Add elements", $"Can't list {field.Name}", problem);
                return;
            }

            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, MaxHeight = 360, FontFamily = FontFamily, FontSize = FontSize };
            foreach (var name in names)
            {
                // TextBlock header: a plain string would turn '_' into an access key
                var item = new MenuItem { Header = new TextBlock { Text = name } };
                item.Click += (_, _) => field.Pattern = name;
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
            DropDown.Track(menu, button);
        }

        #endregion
    }
}
