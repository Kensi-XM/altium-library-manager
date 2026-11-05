using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Altium_Library_Manager.Services;

namespace Altium_Library_Manager
{
    /// <summary>
    /// The help: topics on the left, with a search over all of them, and the chosen topic on the right. Built into the app
    /// rather than a web page, so it works offline and follows the theme. The text is in Help/Help.md. One window at a time,
    /// which stays open beside the main window.
    /// </summary>
    public partial class HelpWindow : Window
    {
        private static HelpWindow? _open;
        private static readonly Brush Found = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xC8, 0x3D));

        private readonly Stack<HelpTopic> _back = new();
        private HelpTopic? _shown;
        private bool _filling;

        private HelpWindow()
        {
            InitializeComponent();
            Closed += (_, _) => _open = null;
            FillList();
        }

        /// <summary>Set by the automated tests, to keep the window off the screen and from taking the focus.</summary>
        public static Action<Window>? BeforeShow;

        /// <summary>Shows the help, at this topic if given; at the one last read otherwise.</summary>
        public static void Open(string? topic = null)
        {
            // A window opened before a dialog is disabled while the dialog is open: a new one, made now, isn't
            if (_open != null && ComponentDispatcher.IsThreadModal)
                _open.Close();
            var window = _open ??= new HelpWindow();
            if (HelpContent.Find(topic) is { } wanted)
                window.ShowTopic(wanted);
            else if (window._shown == null)
                window.ShowTopic(HelpContent.Topics[0]);
            BeforeShow?.Invoke(window);
            if (!window.IsVisible)
                window.Show();
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            if (window.ShowActivated)
                window.Activate();
        }

        /// <summary>The help doesn't outlive the main window.</summary>
        public static void CloseIfOpen() => _open?.Close();

        /// <summary>The topic about this window, if it has one.</summary>
        public static string? TopicFor(Window window) => window switch
        {
            AddElementsWindow => "add-elements",
            NewTableWindow => "new-tables",
            TemplatesWindow => "templates",
            DeletedTablesWindow => "deleted-tables",
            OptionsWindow => "options",
            _ => null
        };

        #region Topic list

        // The topics by group; while searching, just the ones found
        private void FillList()
        {
            _filling = true;
            var query = SearchBox.Text;
            var topics = HelpContent.Search(query);
            TopicList.Items.Clear();
            var searching = query.Trim().Length > 0;
            var style = (Style)FindResource("NavListItem");
            string? group = null;
            foreach (var topic in topics)
            {
                if (!searching && topic.Group != group)
                {
                    group = topic.Group;
                    TopicList.Items.Add(new ListBoxItem
                    {
                        Content = new TextBlock { Text = group, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, TopicList.Items.Count == 0 ? 2 : 10, 0, 2) },
                        IsEnabled = false,
                        Focusable = false,
                        Style = style
                    });
                }
                TopicList.Items.Add(new ListBoxItem { Content = topic.Title, Tag = topic, Style = style });
            }
            NothingFound.Visibility = topics.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SelectInList(_shown);
            _filling = false;
        }

        private void SelectInList(HelpTopic? topic) =>
            TopicList.SelectedItem = TopicList.Items.OfType<ListBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, topic));

        private void TopicList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_filling && (TopicList.SelectedItem as ListBoxItem)?.Tag is HelpTopic topic && !ReferenceEquals(topic, _shown))
                ShowTopic(topic);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            FillList();
            var found = TopicList.Items.OfType<ListBoxItem>().Select(i => i.Tag).OfType<HelpTopic>().ToList();
            // The topic being read stays if it is among those found: its words get marked
            if (found.Count > 0 && (_shown == null || !found.Contains(_shown)))
                ShowTopic(found[0], remember: false);
            else if (_shown != null)
                Render(_shown);
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && TopicList.SelectedItem is ListBoxItem item)
            {
                item.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Page.Focus();
                e.Handled = true;
            }
        }

        #endregion

        #region Page

        private void ShowTopic(HelpTopic topic, bool remember = true)
        {
            if (remember && _shown != null && !ReferenceEquals(_shown, topic))
                _back.Push(_shown);
            _shown = topic;
            BackButton.IsEnabled = _back.Count > 0;
            Title = $"{topic.Title} - Altium Library Manager Help";
            _filling = true;
            SelectInList(topic);
            _filling = false;
            Render(topic);
        }

        private void Render(HelpTopic topic)
        {
            var words = SearchText.Words(SearchBox.Text);
            var document = new FlowDocument
            {
                FontFamily = FontFamily,
                FontSize = 14,
                LineHeight = 21,
                PagePadding = new Thickness(26, 14, 26, 26),
                TextAlignment = TextAlignment.Left,
                IsHyphenationEnabled = false
            };
            document.SetResourceReference(TextElement.ForegroundProperty, "PrimaryTextBrush");

            var title = new Paragraph { FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10), LineHeight = 30 };
            AddText(title.Inlines, topic.Title, words);
            document.Blocks.Add(title);

            foreach (var block in topic.Blocks)
                document.Blocks.Add(Build(block, words));
            Page.Document = document;

            // The first word found, in view; without a search, the start of the page
            Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(Page.Document, document))
                    return;
                if (words.Count > 0 && FirstFound(document) is { } run)
                    run.BringIntoView();
                else
                    (Page.Template?.FindName("PART_ContentHost", Page) as ScrollViewer)?.ScrollToTop();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private static Run? FirstFound(FlowDocument document)
        {
            IEnumerable<Inline> Inlines(Block block) => block switch
            {
                Paragraph p => p.Inlines,
                List l => l.ListItems.SelectMany(i => i.Blocks).SelectMany(Inlines),
                Section s => s.Blocks.SelectMany(Inlines),
                Table t => t.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells).SelectMany(c => c.Blocks).SelectMany(Inlines),
                _ => []
            };
            IEnumerable<Run> Runs(Inline inline) => inline switch
            {
                Run run => [run],
                Span span => span.Inlines.SelectMany(Runs),
                _ => []
            };
            return document.Blocks.SelectMany(Inlines).SelectMany(Runs).FirstOrDefault(r => ReferenceEquals(r.Background, Found));
        }

        private Block Build(HelpBlock block, IReadOnlyList<string> words)
        {
            switch (block.Kind)
            {
                case HelpBlockKind.Heading:
                    var heading = new Paragraph { FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6), LineHeight = 24 };
                    AddLine(heading.Inlines, block.Lines[0], words);
                    return heading;

                case HelpBlockKind.Bullets or HelpBlockKind.Steps:
                    var list = new List
                    {
                        MarkerStyle = block.Kind == HelpBlockKind.Steps ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(0, 4, 0, 10),
                        Padding = new Thickness(24, 0, 0, 0)
                    };
                    foreach (var line in block.Lines)
                    {
                        var item = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
                        AddLine(item.Inlines, line, words);
                        list.ListItems.Add(new ListItem(item));
                    }
                    return list;

                case HelpBlockKind.Tip:
                    var tip = new Paragraph { Margin = new Thickness(0, 8, 0, 12), Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(3, 0, 0, 0) };
                    tip.SetResourceReference(TextElement.BackgroundProperty, "PanelHeaderBrush");
                    tip.SetResourceReference(Block.BorderBrushProperty, "AccentTextFillColorPrimaryBrush");
                    foreach (var line in block.Lines)
                        AddLine(tip.Inlines, line, words);
                    return tip;

                case HelpBlockKind.Table:
                    return BuildTable(block, words);

                default:
                    var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
                    AddLine(paragraph.Inlines, block.Lines[0], words);
                    return paragraph;
            }
        }

        // Two or three columns: the first one narrower, as it names what the others explain
        private Table BuildTable(HelpBlock block, IReadOnlyList<string> words)
        {
            var columns = block.Rows.Max(r => r.Length);
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 12), BorderThickness = new Thickness(0, 1, 0, 0) };
            table.SetResourceReference(Block.BorderBrushProperty, "PanelBorderBrush");
            for (var c = 0; c < columns; c++)
                table.Columns.Add(new TableColumn { Width = new GridLength(c == columns - 1 ? 1.6 : 1, GridUnitType.Star) });

            var group = new TableRowGroup();
            table.RowGroups.Add(group);
            // A first row of plain words is the table's header
            var header = block.Rows[0].All(c => !c.Contains('*') && !c.Contains('`'));
            for (var r = 0; r < block.Rows.Count; r++)
            {
                var row = new TableRow();
                foreach (var text in block.Rows[r])
                {
                    var paragraph = new Paragraph { Margin = new Thickness(0) };
                    if (header && r == 0)
                        paragraph.FontWeight = FontWeights.SemiBold;
                    AddLine(paragraph.Inlines, text, words);
                    var cell = new TableCell(paragraph) { Padding = new Thickness(8, 5, 12, 5), BorderThickness = new Thickness(0, 0, 0, 1) };
                    cell.SetResourceReference(TableCell.BorderBrushProperty, "PanelBorderBrush");
                    if (header && r == 0)
                        cell.SetResourceReference(TextElement.BackgroundProperty, "PanelHeaderBrush");
                    row.Cells.Add(cell);
                }
                group.Rows.Add(row);
            }
            return table;
        }

        private void AddLine(InlineCollection inlines, string line, IReadOnlyList<string> words)
        {
            foreach (var span in HelpContent.Spans(line))
            {
                switch (span.Kind)
                {
                    case HelpSpanKind.Bold:
                        var bold = new Bold();
                        AddText(bold.Inlines, span.Text, words);
                        inlines.Add(bold);
                        break;

                    case HelpSpanKind.Code:
                        var code = new Span { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13 };
                        code.SetResourceReference(TextElement.BackgroundProperty, "PanelHeaderBrush");
                        AddText(code.Inlines, $" {span.Text} ", words);
                        inlines.Add(code);
                        break;

                    case HelpSpanKind.Link:
                        var link = new Hyperlink { TextDecorations = null, Cursor = Cursors.Hand };
                        link.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
                        AddText(link.Inlines, span.Text, words);
                        var target = span.Target;
                        link.Click += (_, _) =>
                        {
                            if (HelpContent.Find(target) is { } topic)
                                GoTo(topic);
                        };
                        inlines.Add(link);
                        break;

                    default:
                        AddText(inlines, span.Text, words);
                        break;
                }
            }
        }

        // The words searched for are marked in the text
        private static void AddText(InlineCollection inlines, string text, IReadOnlyList<string> words)
        {
            var at = 0;
            while (at < text.Length)
            {
                var hit = words.Select(w => (Index: text.IndexOf(w, at, StringComparison.CurrentCultureIgnoreCase), w.Length))
                    .Where(h => h.Index >= 0).OrderBy(h => h.Index).ThenByDescending(h => h.Length).FirstOrDefault();
                if (hit.Length == 0)
                    break;
                if (hit.Index > at)
                    inlines.Add(new Run(text[at..hit.Index]));
                inlines.Add(new Run(text.Substring(hit.Index, hit.Length)) { Background = Found });
                at = hit.Index + hit.Length;
            }
            if (at < text.Length)
                inlines.Add(new Run(text[at..]));
        }

        // A link: the search has done its job, and the other topic is to be read in full
        private void GoTo(HelpTopic topic)
        {
            if (SearchBox.Text.Length > 0)
            {
                var shown = _shown;
                SearchBox.Text = "";
                _shown = shown;
            }
            ShowTopic(topic);
        }

        private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

        private void GoBack()
        {
            if (_back.Count > 0)
                ShowTopic(_back.Pop(), remember: false);
        }

        #endregion

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.F)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.Left)
            {
                GoBack();
                e.Handled = true;
            }
            else if (key == Key.Escape)
            {
                if (SearchBox.Text.Length > 0)
                    SearchBox.Text = "";
                else
                    Close();
                e.Handled = true;
            }
        }
    }
}
