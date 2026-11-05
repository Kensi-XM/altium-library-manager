using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Altium_Library_Manager.Services.Templates;

namespace Altium_Library_Manager
{
    /// <summary>
    /// Edits a list of table fields in place (name, type, size, required, key), with add / remove / reorder.
    /// Used by the New table dialog and the template manager.
    /// </summary>
    public partial class FieldsEditor : UserControl
    {
        public record FieldTypeOption(FieldType Value, string Label);

        public static IReadOnlyList<FieldTypeOption> TypeOptions { get; } =
        [
            new(FieldType.Text, "Text"),
            new(FieldType.LongText, "Long text"),
            new(FieldType.Integer, "Integer"),
            new(FieldType.Number, "Number"),
            new(FieldType.Currency, "Currency"),
            new(FieldType.YesNo, "Yes/No"),
            new(FieldType.DateTime, "Date/Time"),
            new(FieldType.AutoNumber, "AutoNumber")
        ];

        public static readonly DependencyProperty FieldsProperty = DependencyProperty.Register(
            nameof(Fields), typeof(ObservableCollection<TemplateField>), typeof(FieldsEditor),
            new PropertyMetadata(null, (d, e) => ((FieldsEditor)d).OnFieldsReplaced(
                (ObservableCollection<TemplateField>?)e.OldValue, (ObservableCollection<TemplateField>?)e.NewValue)));

        public FieldsEditor()
        {
            InitializeComponent();
            UpdateButtons();
        }

        public ObservableCollection<TemplateField>? Fields
        {
            get => (ObservableCollection<TemplateField>?)GetValue(FieldsProperty);
            set => SetValue(FieldsProperty, value);
        }

        /// <summary>Any edit: a field added, removed, moved or changed, or another list shown.</summary>
        public event EventHandler? Changed;

        private void OnFieldsReplaced(ObservableCollection<TemplateField>? old, ObservableCollection<TemplateField>? fields)
        {
            if (old != null)
            {
                old.CollectionChanged -= Fields_CollectionChanged;
                foreach (var field in old)
                    field.PropertyChanged -= Field_PropertyChanged;
            }
            if (fields != null)
            {
                fields.CollectionChanged += Fields_CollectionChanged;
                foreach (var field in fields)
                    field.PropertyChanged += Field_PropertyChanged;
            }
            UpdateButtons();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void Fields_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            foreach (TemplateField field in e.OldItems ?? Array.Empty<TemplateField>())
                field.PropertyChanged -= Field_PropertyChanged;
            foreach (TemplateField field in e.NewItems ?? Array.Empty<TemplateField>())
                field.PropertyChanged += Field_PropertyChanged;
            UpdateButtons();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void Field_PropertyChanged(object? sender, PropertyChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

        private void UpdateButtons()
        {
            var count = Fields?.Count ?? 0;
            var index = List.SelectedIndex;
            RemoveButton.IsEnabled = index >= 0;
            UpButton.IsEnabled = index > 0;
            DownButton.IsEnabled = index >= 0 && index < count - 1;
            CountText.Text = count == 1 ? "1 field" : $"{count} fields";
        }

        // Clicking or tabbing into a row's box selects the row, so Remove / Up / Down act on the row being edited.
        // (Set here rather than by a style trigger on IsKeyboardFocusWithin: the trigger dropped the selection as soon
        // as the focus left the row, so the toolbar buttons found nothing selected.)
        private void List_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (e.NewFocus is DependencyObject focus && ItemsControl.ContainerFromElement(List, focus) is ListBoxItem item)
                item.IsSelected = true;
        }

        private void Add_Click(object sender, RoutedEventArgs e) => AddField();

        /// <summary>Adds "New field" after the selected field (or at the end) and puts the cursor in its name.</summary>
        private void AddField()
        {
            if (Fields == null)
                return;
            var name = "New field";
            for (var i = 2; Contains(name); i++)
                name = $"New field {i}";
            FocusName(Insert(new TemplateField { Name = name }), selectAll: true);
        }

        private bool Contains(string name) =>
            Fields?.Any(f => string.Equals(f.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) == true;

        /// <summary>Inserts after the selected field (or at the end) and selects it.</summary>
        private TemplateField Insert(TemplateField field)
        {
            var index = List.SelectedIndex >= 0 ? List.SelectedIndex + 1 : Fields!.Count;
            Fields!.Insert(index, field);
            List.SelectedItem = field;
            List.ScrollIntoView(field);
            return field;
        }

        /// <summary>Menu of the usual Altium fields in groups; the ones already in the list can't be added again.</summary>
        private void Standard_Click(object sender, RoutedEventArgs e)
        {
            if (Fields == null || DropDown.WasJustClosed(StandardButton))
                return;
            var menu = new ContextMenu { PlacementTarget = StandardButton, Placement = PlacementMode.Bottom };
            foreach (var group in BuiltInTemplates.StandardFields)
            {
                if (menu.Items.Count > 0)
                    menu.Items.Add(new Separator());
                foreach (var name in group)
                {
                    var item = new MenuItem { Header = name, IsEnabled = !Contains(name) };
                    item.Click += (_, _) =>
                    {
                        var key = name == "Part Number";
                        Insert(new TemplateField { Name = name, Required = key, PrimaryKey = key });
                    };
                    menu.Items.Add(item);
                }
            }
            menu.IsOpen = true;
            DropDown.Track(menu, StandardButton);
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            var index = List.SelectedIndex;
            if (Fields == null || index < 0)
                return;
            Fields.RemoveAt(index);
            if (Fields.Count > 0)
                List.SelectedIndex = Math.Min(index, Fields.Count - 1);
        }

        private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);

        private void Down_Click(object sender, RoutedEventArgs e) => Move(1);

        private void Move(int offset)
        {
            var index = List.SelectedIndex;
            if (Fields == null || index < 0 || index + offset < 0 || index + offset >= Fields.Count)
                return;
            var field = Fields[index];
            Fields.Move(index, index + offset);
            List.SelectedItem = field;
            List.ScrollIntoView(field);
            FocusName(field, selectAll: false);
        }

        private void List_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                AddField();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down)
            {
                Move(key == Key.Up ? -1 : 1);
                e.Handled = true;
            }
        }

        #region Dragging rows

        private Point? _dragStart;

        private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(List);
            if (((FrameworkElement)sender).DataContext is TemplateField field)
                List.SelectedItem = field;
            e.Handled = true;
        }

        private void Grip_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed ||
                ((FrameworkElement)sender).DataContext is not TemplateField field)
            {
                _dragStart = null;
                return;
            }
            var offset = e.GetPosition(List) - start;
            if (Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _dragStart = null;
            DragDrop.DoDragDrop(List, new DataObject(typeof(TemplateField), field), DragDropEffects.Move);
            FocusName(field, selectAll: false);
        }

        /// <summary>The row follows the mouse: it moves into the place of the row under the pointer.</summary>
        private void List_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(TemplateField)) is not TemplateField field || Fields == null)
                return;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            // Scroll when near the top or bottom edge
            var position = e.GetPosition(List);
            if (FindDescendant<ScrollViewer>(List) is { } scroll)
            {
                if (position.Y < 24)
                    scroll.LineUp();
                else if (position.Y > List.ActualHeight - 24)
                    scroll.LineDown();
            }

            if (List.InputHitTest(position) is not DependencyObject over ||
                ItemsControl.ContainerFromElement(List, over) is not ListBoxItem { DataContext: TemplateField target } || target == field)
                return;
            int from = Fields.IndexOf(field), to = Fields.IndexOf(target);
            if (from >= 0 && to >= 0)
            {
                Fields.Move(from, to);
                List.SelectedItem = field;
            }
        }

        private void List_Drop(object sender, DragEventArgs e) => e.Handled = e.Data.GetDataPresent(typeof(TemplateField));

        #endregion

        /// <summary>Focuses the field's name box once its row has been created.</summary>
        private void FocusName(TemplateField field, bool selectAll)
        {
            List.ScrollIntoView(field);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (List.ItemContainerGenerator.ContainerFromItem(field) is not ListBoxItem item)
                    return;
                var box = FindDescendant<TextBox>(item);
                if (box == null)
                    return;
                box.Focus();
                if (selectAll)
                    box.SelectAll();
                else
                    box.CaretIndex = box.Text.Length;
            });
        }

        private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                    return match;
                if (FindDescendant<T>(child) is { } found)
                    return found;
            }
            return null;
        }
    }
}
