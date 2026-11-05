using System.Data;
using System.Windows.Controls;
using static Altium_Library_Manager.Tests.T;

namespace Altium_Library_Manager.Tests
{
    /// <summary>
    /// Thousands of random actions on a table, the way a long session would go: editing, adding, deleting, pasting,
    /// undoing, searching, saving. After every save what the table shows has to be what the database holds, and undoing
    /// everything has to give the starting table back. A failure names the seed, which repeats the same actions.
    /// </summary>
    public static class StressTests
    {
        private static void Session(int seed, bool keyed, int steps, bool saving = true)
        {
            using var test = new TestDatabase();
            const string name = "S";
            if (keyed)
                test.CreateKeyedTable(name, 30);
            else
                test.CreateKeylessTable(name);
            using var host = new TabHost(test.Database, name);
            var tab = host.Tab;
            var random = new Random(seed);
            var start = TabHost.Contents(test.Database.LoadTable(name));
            var fresh = 0;
            var log = new List<string>();

            // The rows of the table without a key start with equal part numbers: undoing a rename of one of them gives a
            // row Save rightly refuses. It keeps the changes; anything else that stops a save is a fault.
            bool Saved(string when)
            {
                if (tab.Save(host.Window))
                    return true;
                var message = TableTests.Dialogs.LastOrDefault() ?? "";
                True(!keyed && message.Contains("already has the Part Number"), $"{when}: not saved: {message}");
                True(tab.IsDirty, $"{when}: the changes were lost");
                return false;
            }

            DataRowView? Any() => host.Items.Where(v => !v.IsNew).ToList() is { Count: > 0 } rows ? rows[random.Next(rows.Count)] : null;
            string Column() => random.Next(4) switch { 0 => "Description", 1 => "Value", 2 => "Notes", _ => "Part Number" };
            void Pick(int cells)
            {
                var first = Any();
                if (first == null)
                    return;
                var column = Column();
                tab.SelectItem(first, host.Column(column));
                for (var i = 1; i < cells; i++)
                    if (Any() is { } more && !host.Grid.SelectedCells.Contains(new DataGridCellInfo(more, host.Column(column))))
                        host.Grid.SelectedCells.Add(new DataGridCellInfo(more, host.Column(column)));
            }

            for (var step = 0; step < steps; step++)
            {
                var action = random.Next(100);
                switch (action)
                {
                    case < 20:
                        log.Add("change");
                        if (Any() is { } row)
                            tab.ChangeField(row.Row, Column(), $"v{random.Next(1000)}");
                        break;
                    case < 30:
                        log.Add("clear");
                        Pick(random.Next(1, 4));
                        tab.ClearSelectedCells();
                        break;
                    case < 40:
                        log.Add("fill");
                        Pick(random.Next(1, 4));
                        T.Call(tab, "SetSelectedCells", "Fill", $"f{random.Next(1000)}");
                        break;
                    case < 46:
                        log.Add("fill down");
                        Pick(random.Next(2, 5));
                        tab.FillDown();
                        break;
                    case < 52:
                        log.Add("paste");
                        Pick(1);
                        T.Call(tab, "PasteText", $"p{random.Next(1000)}\tq\np{random.Next(1000)}\tr");
                        break;
                    case < 60:
                        log.Add("add");
                        tab.AddRows(Enumerable.Range(0, random.Next(1, 4)).Select(_ =>
                            (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["Part Number"] = $"S{fresh++}", ["Value"] = "1R", ["Notes"] = new string('z', random.Next(600)) }).ToList());
                        break;
                    case < 68:
                        log.Add("delete");
                        Pick(random.Next(1, 3));
                        tab.DeleteSelectedRows();
                        break;
                    case < 82:
                        log.Add("undo");
                        tab.Undo();
                        break;
                    case < 90:
                        log.Add("redo");
                        tab.Redo();
                        break;
                    case < 92:
                        log.Add("search");
                        tab.Query = random.Next(2) == 0 ? "" : "v";
                        tab.ApplySearchNow();
                        break;
                    case < 94:
                        log.Add("sort");
                        if (keyed)
                            T.Call(host.Grid, "PerformSort", host.Column(random.Next(3) == 0 ? "Value" : random.Next(2) == 0 ? "Temp, Max" : "Qty, Min"));
                        break;
                    case < 95:
                        log.Add("options");
                        TableTab.Columns.FontSize = 10 + random.Next(7);
                        TableTab.Columns.HighlightCurrentRow = random.Next(2) == 0;
                        TableTab.Columns.ColumnSizing = random.Next(2) == 0 ? Altium_Library_Manager.Services.Settings.ColumnSizing.Fixed : Altium_Library_Manager.Services.Settings.ColumnSizing.FitContent;
                        TableTab.RowHeight = 20 + 4 * random.Next(3);
                        TableTab.AlternatingRows = random.Next(2) == 0;
                        if (saving)
                            TableTab.Editing.UndoSteps = 50 + 50 * random.Next(10);
                        tab.ApplyGridSettings();
                        break;
                    case >= 95 when !saving:
                        break;
                    default:
                        log.Add("save");
                        if (Saved($"seed {seed} step {step} [{string.Join(" ", log.TakeLast(12))}]"))
                            host.CheckSameAsDatabase($"seed {seed} step {step} [{string.Join(" ", log.TakeLast(12))}]");
                        break;
                }
                if (step % 25 == 0)
                    T.Wait(1);
                True(tab.SelectedRow == null || tab.SelectedRow.IsNew || tab.SelectedRow.Row.RowState != DataRowState.Deleted, $"seed {seed} step {step}: a deleted row is selected");
            }

            TableTab.Editing.UndoSteps = 500;
            TableTab.Columns.FontSize = 13;
            TableTab.Columns.HighlightCurrentRow = true;
            TableTab.Columns.ColumnSizing = Altium_Library_Manager.Services.Settings.ColumnSizing.FitContent;
            TableTab.RowHeight = 20;
            TableTab.AlternatingRows = false;
            tab.Query = "";
            tab.ApplySearchNow();
            T.Wait(1);

            if (!saving)
            {
                // Every step undone gives the table as it was read, every step redone the table as it was left
                var end = Sorted(tab);
                var count = 0;
                for (; tab.UndoDescription != null; count++)
                    tab.Undo();
                Equal(Sorted(start), Sorted(tab), $"seed {seed}: after undoing {count} steps");
                True(!tab.IsDirty, $"seed {seed}: nothing to save after undoing all");
                for (var i = 0; i < count; i++)
                    tab.Redo();
                Equal(end, Sorted(tab), $"seed {seed}: after redoing");
            }
            if (!Saved($"seed {seed} at the end"))
                tab.Discard();
            host.CheckSameAsDatabase($"seed {seed} at the end");
        }

        private static string Sorted(TableTab tab) => Sorted(TabHost.Contents(tab.Table!));

        private static string Sorted(IEnumerable<string> rows) => string.Join("\n", rows.Order(StringComparer.Ordinal));

        public static void Run(int sessions, int steps)
        {
            Section($"Random sessions ({sessions} x {steps} actions, four ways)");
            for (var seed = 1; seed <= sessions; seed++)
            {
                var number = seed;
                Test($"table with a key, seed {number}", () => Session(number, keyed: true, steps));
                Test($"table without a key, seed {number}", () => Session(number + 1000, keyed: false, steps));
                Test($"undo and redo of everything, key, seed {number}", () => Session(number + 2000, keyed: true, steps, saving: false));
                Test($"undo and redo of everything, no key, seed {number}", () => Session(number + 3000, keyed: false, steps, saving: false));
            }
        }
    }
}
