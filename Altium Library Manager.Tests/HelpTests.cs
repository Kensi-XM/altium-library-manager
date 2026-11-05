using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Altium_Library_Manager.Services;
using static Altium_Library_Manager.Tests.T;

namespace Altium_Library_Manager.Tests
{
    /// <summary>The text of the help, and its window.</summary>
    public static class HelpTests
    {
        public static void RunText()
        {
            Section("Help text");
            var topics = HelpContent.Topics;
            Test("the topics are there", () =>
            {
                True(topics.Count >= 12, $"{topics.Count} topics");
                Equal("start", topics[0].Id, "first");
                Equal(topics.Count, topics.Select(t => t.Id).Distinct().Count(), "ids are unique");
                True(topics.All(t => t.Group.Length > 0 && t.Title.Length > 0 && t.Blocks.Count > 0), "complete");
            });
            Test("every link leads to a topic", () =>
            {
                foreach (var topic in topics)
                    foreach (var line in topic.Blocks.SelectMany(b => b.Lines.Concat(b.Rows.SelectMany(r => r))))
                        foreach (var span in HelpContent.Spans(line).Where(s => s.Kind == HelpSpanKind.Link))
                            True(HelpContent.Find(span.Target) != null, $"{topic.Id}: link to {span.Target}");
            });
            Test("no markup is left in the text", () =>
            {
                foreach (var topic in topics)
                    foreach (var line in topic.Blocks.SelectMany(b => b.Lines.Concat(b.Rows.SelectMany(r => r))))
                        foreach (var span in HelpContent.Spans(line))
                            True(!span.Text.Contains("**") && !span.Text.Contains('`') && !span.Text.Contains("](#"), $"{topic.Id}: [{line}]");
            });
            Test("each table has rows of the same length", () =>
            {
                foreach (var topic in topics)
                    foreach (var table in topic.Blocks.Where(b => b.Kind == HelpBlockKind.Table))
                        True(table.Rows.All(r => r.Length == table.Rows[0].Length), $"{topic.Id}: [{string.Join(" | ", table.Rows[0])}]");
            });
            Test("the windows' topics exist", () =>
            {
                foreach (var id in new[] { "add-elements", "new-tables", "templates", "deleted-tables", "options", "shortcuts", "problems" })
                    True(HelpContent.Find(id) != null, id);
            });
            Test("the menus' shortcuts are in the help", () =>
            {
                var shortcuts = HelpContent.Find("shortcuts")!.SearchText;
                var xaml = typeof(App).Assembly.GetManifestResourceNames();
                foreach (var keys in new[] { "F1", "Ctrl+O", "Ctrl+N", "Ctrl+S", "Ctrl+Shift+S", "Ctrl+Z", "Ctrl+Y", "Ctrl+E", "Ctrl+Plus", "Ctrl+D", "Ctrl+Minus", "F5", "Ctrl+,", "Ctrl+W", "Ctrl+C", "Ctrl+X", "Ctrl+V", "Del", "Ctrl+1", "Alt+9", "Ctrl+Tab" })
                    True(shortcuts.Contains(keys), keys);
            });
            foreach (var (query, topic) in new[]
                     {
                         ("template", "templates"), ("shift sort", "search"), ("fill down", "cells"), ("E96", null), ("E192", "add-elements"), ("{Value}", "add-elements"),
                         ("settings.json", "problems"), ("deleted", "deleted-tables"), ("undo", "editing"), ("datasheet", null), ("LAYOUT", "window")
                     })
                Test($"search finds [{query}]", () =>
                {
                    var found = HelpContent.Search(query);
                    if (topic != null)
                        True(found.Any(t => t.Id == topic), string.Join(", ", found.Select(t => t.Id)));
                });
            Test("search: all words, nothing for nonsense, everything for nothing", () =>
            {
                Equal(0, HelpContent.Search("qqqzzz").Count, "nonsense");
                Equal(topics.Count, HelpContent.Search("  ").Count, "empty");
                True(HelpContent.Search("templates").First().Id == "templates", "title first");
                True(HelpContent.Search("\"Part Number\" unique").Count >= 1, "a phrase");
            });
            Test("a missing Access engine is explained; other errors stay as they are", () =>
            {
                var missing = AccessDatabase.ExplainOpenError(new InvalidOperationException("The 'Microsoft.ACE.OLEDB.12.0' provider is not registered on the local machine."));
                True(missing.Contains("accessdatabaseengine_X64.exe"), missing);
                Equal("Disk full", AccessDatabase.ExplainOpenError(new IOException("Disk full")), "other");
            });
            Test("parsing odd text doesn't fail", () =>
            {
                HelpContent.Parse("");
                HelpContent.Parse("text before any topic\n");
                var parsed = HelpContent.Parse("= G\n# a\n\n| x\n- y\n1. z\n> t\n## h\n**unclosed `code [x](#a\n");
                Equal("a", parsed[0].Title, "id as title");
                HelpContent.Spans("** ` [](#)");
            });
        }

        public static void RunWindow()
        {
            Section("Help window");
            HelpWindow Shown() => (HelpWindow)typeof(HelpWindow).GetField("_open", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            V Part<V>(string name) => (V)Shown().FindName(name);
            string PageText()
            {
                var document = Part<FlowDocumentScrollViewer>("Page").Document;
                return new TextRange(document.ContentStart, document.ContentEnd).Text;
            }
            HelpTopic Current() => (HelpTopic)typeof(HelpWindow).GetField("_shown", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Shown())!;

            try
            {
                Test("it opens at the first topic, off the screen for the test", () =>
                {
                    HelpWindow.BeforeShow = window =>
                    {
                        window.ShowActivated = false;
                        window.ShowInTaskbar = false;
                        window.WindowStartupLocation = WindowStartupLocation.Manual;
                        window.Left = -3000;
                    };
                    HelpWindow.Open();
                    Wait(20);
                    Equal("start", Current().Id, "topic");
                    True(PageText().Contains("Your first five minutes"), "text");
                });
                Test("every topic shows, in both themes", () =>
                {
                    foreach (var theme in new[] { Altium_Library_Manager.Services.Settings.AppTheme.Light, Altium_Library_Manager.Services.Settings.AppTheme.Dark })
                    {
                        ThemeManager.Apply(theme);
                        foreach (var topic in HelpContent.Topics)
                        {
                            HelpWindow.Open(topic.Id);
                            Wait(5);
                            Equal(topic.Id, Current().Id, "shown");
                            True(PageText().Contains(topic.Title), topic.Id);
                        }
                    }
                });
                Test("all the text of a topic reaches the page", () =>
                {
                    foreach (var topic in HelpContent.Topics)
                    {
                        HelpWindow.Open(topic.Id);
                        Wait(2);
                        var page = Regex.Replace(PageText().Replace(' ', ' '), @"\s+", "");
                        foreach (var line in topic.Blocks.SelectMany(b => b.Lines.Concat(b.Rows.SelectMany(r => r))))
                            foreach (var span in HelpContent.Spans(line))
                                True(page.Contains(Regex.Replace(span.Text, @"\s+", "")), $"{topic.Id}: [{span.Text}]");
                    }
                });
                Test("opening a topic again doesn't add a second window", () =>
                {
                    var window = Shown();
                    HelpWindow.Open("options");
                    HelpWindow.Open("options");
                    True(ReferenceEquals(window, Shown()), "one window");
                    Equal(1, Application.Current.Windows.OfType<HelpWindow>().Count(), "windows");
                });
                Test("Back returns through the topics read", () =>
                {
                    HelpWindow.Open("search");
                    HelpWindow.Open("editing");
                    HelpWindow.Open("cells");
                    Call(Shown(), "GoBack");
                    Equal("editing", Current().Id, "one back");
                    Call(Shown(), "GoBack");
                    Equal("search", Current().Id, "two back");
                });
                Test("the search filters the list, marks the words, and clearing brings all back", () =>
                {
                    var box = Part<TextBox>("SearchBox");
                    var list = Part<ListBox>("TopicList");
                    box.Text = "fill down";
                    Wait(5);
                    var found = list.Items.OfType<ListBoxItem>().Count();
                    True(found >= 1 && found < HelpContent.Topics.Count, $"{found} topics");
                    True(list.SelectedItem != null, "one is selected");
                    True(HelpContent.Search("fill down").Contains(Current()), "the page is one found");
                    box.Text = "qqqzzz";
                    Wait(5);
                    Equal(0, list.Items.Count, "no topics");
                    Equal(Visibility.Visible, Part<TextBlock>("NothingFound").Visibility, "says so");
                    box.Text = "";
                    Wait(5);
                    True(list.Items.Count > HelpContent.Topics.Count, "topics and group titles");
                    Equal(Visibility.Collapsed, Part<TextBlock>("NothingFound").Visibility, "message gone");
                    True(list.SelectedItem != null, "the topic stays selected");
                });
                Test("odd search text doesn't fail", () =>
                {
                    var box = Part<TextBox>("SearchBox");
                    foreach (var text in new[] { "\"", "*", "**", "`", "[", "(#", "\\", "{", "a a a a", " ", new string('x', 5000), "\"\"" })
                    {
                        box.Text = text;
                        Wait(2);
                    }
                    box.Text = "";
                });
                Test("clicking a topic in the list shows it", () =>
                {
                    var list = Part<ListBox>("TopicList");
                    var item = list.Items.OfType<ListBoxItem>().First(i => (i.Tag as HelpTopic)?.Id == "templates");
                    list.SelectedItem = item;
                    Wait(5);
                    Equal("templates", Current().Id, "topic");
                });
                Test("a topic that doesn't exist leaves the page", () =>
                {
                    HelpWindow.Open("no-such-topic");
                    Equal("templates", Current().Id, "topic");
                });
                void PressF1(Window window) => window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.F1) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                Test("F1 in a dialog opens its topic; F1 elsewhere keeps the page; F1 in the help does nothing", () =>
                {
                    Equal(null, HelpWindow.TopicFor(new Window()), "other windows");
                    var options = new OptionsWindow(new Altium_Library_Manager.Services.Settings.UserSettings(), _ => { })
                    {
                        ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, ShowInTaskbar = false
                    };
                    options.Show();
                    Wait(5);
                    HelpWindow.Open("search");
                    PressF1(options);
                    Wait(5);
                    Equal("options", Current().Id, "the dialog's topic");
                    options.Close();
                    var other = new Window { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, ShowInTaskbar = false, Width = 50, Height = 50 };
                    other.Show();
                    Wait(5);
                    PressF1(other);
                    Wait(5);
                    Equal("options", Current().Id, "stays");
                    other.Close();
                    PressF1(Shown());
                    Wait(5);
                    Equal(1, Application.Current.Windows.OfType<HelpWindow>().Count(), "one window");
                });
                Test("F1 in a modal dialog gives a help window that can be used, and it stays after the dialog", () =>
                {
                    var before = Shown();
                    var modal = new Window { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, ShowInTaskbar = false, Width = 50, Height = 50 };
                    modal.Loaded += (_, _) => modal.Dispatcher.BeginInvoke(() =>
                    {
                        PressF1(modal);
                        Wait(10);
                        modal.Close();
                    }, System.Windows.Threading.DispatcherPriority.Background);
                    modal.ShowDialog();
                    Wait(10);
                    True(!ReferenceEquals(before, Shown()), "a new window, as the old one was disabled");
                    Equal(1, Application.Current.Windows.OfType<HelpWindow>().Count(), "one window");
                    True(Shown().IsVisible, "still there");
                });
                Test("closing and opening again works", () =>
                {
                    HelpWindow.CloseIfOpen();
                    Wait(5);
                    Equal(0, Application.Current.Windows.OfType<HelpWindow>().Count(), "closed");
                    HelpWindow.CloseIfOpen();
                    HelpWindow.Open("options");
                    Wait(5);
                    Equal("options", Current().Id, "topic");
                });
                Test("About shows the version and what it runs on", () =>
                {
                    var about = new AboutWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, ShowInTaskbar = false };
                    about.Show();
                    Wait(10);
                    True(Regex.IsMatch(AboutWindow.AppVersion, @"^\d+\.\d+\.\d+$"), AboutWindow.AppVersion);
                    var details = AboutWindow.Details().ToList();
                    True(details.Count >= 5, "lines");
                    True(details.All(d => !d.Contains("not found")), string.Join("; ", details));
                    about.Close();
                });
            }
            finally
            {
                ThemeManager.Apply(Altium_Library_Manager.Services.Settings.AppTheme.Dark);
                HelpWindow.CloseIfOpen();
                HelpWindow.BeforeShow = null;
            }
        }
    }
}
