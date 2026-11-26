using System.ComponentModel;
using System.Data;
using System.Windows.Controls;
using static Altium_Library_Manager.Tests.T;
using static Altium_Library_Manager.Tests.TableTests;

namespace Altium_Library_Manager.Tests
{
    /// <summary>Columns with a comma in their name: DataView.Sort can't name them, so they sort through a hidden twin.</summary>
    public static class SortTests
    {
        /// <summary>The sort key alone: compared as the grid compares text (current culture, case ignored).</summary>
        public static void RunKeys()
        {
            Section("Sort keys: numbers by value");
            var compare = StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture, true);
            void Sorted(string what, params string[] expected)
            {
                var shuffled = expected.Reverse().Concat(expected.Where((_, i) => i % 2 == 0)).Distinct().ToList();
                Equal(string.Join(" | ", expected), string.Join(" | ", shuffled.OrderBy(Altium_Library_Manager.Services.SortKey.For, compare)), what);
            }
            Test("resistor descriptions", () => Sorted("descriptions",
                "RES SMD 0 Ohm 1/10W 0603", "RES SMD 1 Ohm 1% 1/10W 0603", "RES SMD 1.02 Ohm 1% 1/10W 0603", "RES SMD 9.76 Ohm 1% 1/10W 0603",
                "RES SMD 10 Ohm 1% 1/10W 0603", "RES SMD 999 Ohm 1% 1/10W 0603", "RES SMD 1 kOhm 1% 1/10W 0603", "RES SMD 4.7 kOhm 1% 1/10W 0603",
                "RES SMD 4.75 kOhm 1% 1/10W 0603", "RES SMD 6.49 kOhm 1% 1/10W 0603", "RES SMD 1 MOhm 1% 1/10W 0603"));
            Test("part numbers with R, K and M codes", () => Sorted("codes",
                "0R_0603", "1R_0603", "1R02_0603", "4R7_0603", "10R_0603", "11R5_0603", "1K_0603", "1K02_0603", "4K7_0603", "4K75_0603", "10K_0603", "1M_0603", "1M5_0603"));
            Test("capacitors and inductors", () => Sorted("prefixes",
                "1pF", "4p7", "100pF", "1nF", "4n7", "100nF", "1uF", "2u2", "4.7 µF", "10µF", "100 uF", "1mF"));
            Test("plain numbers: 2 before 10, leading zeros", () => Sorted("numbers",
                "C1", "C2", "C9", "C10", "C11", "C100", "SOT23-3", "SOT23-5", "SOT223"));
            Test("words keep their letters: M3, 2 Max, 0603_N", () => Sorted("words",
                "Bolt M3", "Bolt M4", "Bolt M10", "RES 0402_N", "RES 0603_L", "RES 0603_M", "RES 0603_N", "RES 1206_N"));
            Test("comma decimals and Russian units", () => Sorted("russian",
                "2,2 мкФ", "100 мкФ", "4,7 Ом", "47 Ом", "1 кОм", "4,7 кОм", "1 МОм"));
            Test("same value, different spelling: same key", () =>
            {
                Equal(Altium_Library_Manager.Services.SortKey.For("4K7"), Altium_Library_Manager.Services.SortKey.For("4.7k"), "4K7 = 4.7k");
                Equal(Altium_Library_Manager.Services.SortKey.For("4700"), Altium_Library_Manager.Services.SortKey.For("4.7k"), "4700 = 4.7k");
                Equal("Resistor", Altium_Library_Manager.Services.SortKey.For("Resistor"), "no digits: as it is");
                Equal(Altium_Library_Manager.Services.SortKey.For("0"), Altium_Library_Manager.Services.SortKey.For("0.000"), "zero");
            });
            Test("long digit runs and odd text don't throw", () =>
            {
                foreach (var text in new[] { "", "0", "000000000000000000000000000000001", new string('9', 2000), "1e999", "1.", ".5", "1..2", "k1", "1kk", "4K7K7", "1 k", "µ", "1µ" })
                    Altium_Library_Manager.Services.SortKey.For(text);
                // Digits of other scripts are text: only 0-9 make the key's numbers
                True(Altium_Library_Manager.Services.SortKey.For("1٤٧").EndsWith("٤٧", StringComparison.Ordinal), "other digits kept as they are");
                Equal(18 + 2, Altium_Library_Manager.Services.SortKey.For("1٤٧").Length, "one number");
            });
        }

        public static void Run()
        {
            Section("Sorting columns with a comma in their names");
            using var test = new TestDatabase();
            test.CreateKeyedTable("T", 12);
            using var host = new TabHost(test.Database, "T");
            var tab = host.Tab;
            var grid = host.Grid;

            void Click(string column)
            {
                Call(grid, "PerformSort", host.Column(column));
                Wait(5);
            }
            void Unsorted()
            {
                tab.Discard();
                tab.Query = "";
                tab.ApplySearchNow();
                grid.Items.SortDescriptions.Clear();
                foreach (var column in grid.Columns)
                    column.SortDirection = null;
                Dialogs.Clear();
                Wait(5);
            }
            List<string> Shown(string column) => host.Items.Select(v => Text(v, column)).ToList();
            void InOrder(IEnumerable<string> expected, string column, string what) =>
                Equal(string.Join(" ", expected), string.Join(" ", Shown(column)), what);

            Test("the hidden twins are no fields", () =>
            {
                True(tab.Table!.Columns.Count > TableTab.FieldColumns(tab.Table).Count(), "there are twins");
                Equal(TableTab.FieldColumns(tab.Table).Count(), grid.Columns.Count, "no column for them");
                True(grid.Columns.All(c => c.Header is string name && !TableTab.IsSortTwin(tab.Table.Columns[name]!)), "headers");
                True(!tab.IsDirty, "a twin changes nothing");
            });
            Test("text: up, then down, with the arrow on the header", () =>
            {
                var values = Shown("Temp, Max");
                Click("Temp, Max");
                InOrder(values.Order(StringComparer.Ordinal), "Temp, Max", "up");
                Equal(ListSortDirection.Ascending, host.Column("Temp, Max").SortDirection, "arrow");
                Click("Temp, Max");
                InOrder(values.OrderDescending(StringComparer.Ordinal), "Temp, Max", "down");
                Equal(ListSortDirection.Descending, host.Column("Temp, Max").SortDirection, "arrow down");
                Unsorted();
            });
            Test("numbers sort as numbers, not as text", () =>
            {
                Click("Qty, Min");
                InOrder(Enumerable.Range(1, 12).Select(i => i * 7 % 12 + 1).Order().Select(i => i.ToString()), "Qty, Min", "numbers");
                Unsorted();
            });
            Test("empty fields come first; case is ignored", () =>
            {
                Equal(null, tab.ChangeField(host.Item("PN-005").Row, "Temp, Max", ""), "cleared");
                Equal(null, tab.ChangeField(host.Item("PN-006").Row, "Temp, Max", "t055"), "small letters");
                Click("Temp, Max");
                Equal("", Shown("Temp, Max")[0], "first");
                var values = Shown("Temp, Max");
                True(values.IndexOf("t055") > values.IndexOf("T05"), "t055 after T05");
                Unsorted();
            });
            Test("another column takes over, and the arrow moves", () =>
            {
                Click("Temp, Max");
                Click("Size / Dimension");
                Equal(null, host.Column("Temp, Max").SortDirection, "old arrow");
                Equal(ListSortDirection.Ascending, host.Column("Size / Dimension").SortDirection, "new arrow");
                Equal(1, grid.Items.SortDescriptions.Count, "one sort");
                Unsorted();
            });
            Test("two columns at once, as with Shift", () =>
            {
                grid.Items.SortDescriptions.Add(new SortDescription(host.Column("Active").SortMemberPath, ListSortDirection.Ascending));
                grid.Items.SortDescriptions.Add(new SortDescription(host.Column("Qty, Min").SortMemberPath, ListSortDirection.Ascending));
                Wait(5);
                var rows = host.Items.Select(v => ((bool)v["Active"], (int)v["Qty, Min"])).ToList();
                Equal(string.Join(" ", rows.OrderBy(r => r.Item1).ThenBy(r => r.Item2)), string.Join(" ", rows), "both");
                Unsorted();
            });
            Test("search keeps the sort, and the sort the search", () =>
            {
                Click("Qty, Min");
                tab.Query = "PN-00";
                tab.ApplySearchNow();
                Wait(5);
                Equal(9, host.Items.Count, "found");
                InOrder(Shown("Qty, Min").Select(int.Parse).Order().Select(i => i.ToString()), "Qty, Min", "sorted");
                Click("Qty, Min");
                Equal(9, host.Items.Count, "still found");
                Equal("9 of 12 rows", tab.CountText, "count");
                tab.Query = "";
                tab.ApplySearchNow();
                Wait(5);
                Equal(12, host.Items.Count, "all");
                Equal(ListSortDirection.Descending, host.Column("Qty, Min").SortDirection, "arrow stays");
                InOrder(Shown("Qty, Min").Select(int.Parse).OrderDescending().Select(i => i.ToString()), "Qty, Min", "still sorted");
                Unsorted();
            });
            Test("a search doesn't find a value by its twin twice, and a chosen column works", () =>
            {
                tab.SetSearchColumns(["Temp, Max"]);
                tab.Query = "T05";
                tab.ApplySearchNow();
                Wait(5);
                Equal(1, host.Items.Count, "found");
                tab.SetSearchColumns([]);
                Unsorted();
            });
            Test("an edit moves the row as in any sorted column; undo and save work", () =>
            {
                Click("Temp, Max");
                var before = string.Join(" ", Shown("Part Number"));
                var part = Text(host.Items[2], "Part Number");
                TypeInCell(host, host.Items[2], "Temp, Max", "ZZZ");
                Equal(part, Text(host.Items[^1], "Part Number"), "moved to the end");
                tab.Undo();
                Wait(5);
                Equal(before, string.Join(" ", Shown("Part Number")), "back in its place");
                True(!tab.IsDirty, "dirty: " + Why(tab));
                TypeInCell(host, host.Item(part), "Temp, Max", "ZZZ");
                True(tab.Save(host.Window), "saved");
                host.CheckSameAsDatabase("after saving in a sorted table");
                tab.Undo();
                True(tab.Save(host.Window), "saved back");
                host.CheckSameAsDatabase("after saving the undo");
                Unsorted();
            });
            Test("deleting in a sorted table, undo, and the row count", () =>
            {
                Click("Qty, Min");
                var part = Text(host.Items[3], "Part Number");
                host.Select("Value", host.Items[3]);
                tab.DeleteSelectedRows();
                Wait(5);
                Equal(11, host.Items.Count, "gone");
                Equal("11 rows", tab.CountText, "count");
                True(!Shown("Part Number").Contains(part), "not shown");
                tab.Undo();
                Wait(5);
                Equal(12, host.Items.Count, "back");
                True(!tab.IsDirty, "dirty: " + Why(tab));
                Unsorted();
            });
            Test("rows added in a sorted table, by Add elements and in the last row", () =>
            {
                Click("Temp, Max");
                tab.AddRows([new Dictionary<string, string> { ["Part Number"] = "ADD-1" }]);
                Wait(5);
                True(Shown("Part Number").Contains("ADD-1"), "added");
                tab.AddRow(duplicate: false);
                Wait(5);
                var column = grid.CurrentColumn!;
                var box = (TextBox)column.GetCellContent((DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(grid.CurrentItem));
                Equal("Part Number", column.Header, "starts in Part Number");
                box.Text = "ADD-2";
                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                Wait(5);
                True(Shown("Part Number").Contains("ADD-2"), "typed row shown");
                Equal(14, host.Items.Count, "rows");
                True(tab.Save(host.Window), "saved");
                host.CheckSameAsDatabase("after adding in a sorted table");
                tab.Undo();
                tab.Undo();
                Wait(5);
                Equal(12, host.Items.Count, "undone");
                True(tab.Save(host.Window), "saved back");
                Unsorted();
            });
            Test("a duplicate copies the fields, not the twins", () =>
            {
                tab.SelectItem(host.Items[1], host.Column("Value"));
                var source = host.Items[1];
                tab.AddRow(duplicate: true);
                Wait(5);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                Wait(5);
                var copy = host.Item($"{Text(source, "Part Number")} (2)");
                Equal(Text(source, "Temp, Max"), Text(copy, "Temp, Max"), "field");
                True(tab.Save(host.Window), "saved");
                host.CheckSameAsDatabase("after a duplicate");
                tab.Undo();
                True(tab.Save(host.Window), "saved back");
                Unsorted();
            });
            Test("copy and fill down follow the sorted rows; the selected row stays", () =>
            {
                Click("Qty, Min");
                var top = host.Items[0];
                host.Select("Value", host.Items[0], host.Items[1]);
                Equal($"{Text(host.Items[0], "Value")}\r\n{Text(host.Items[1], "Value")}\r\n", Call(tab, "SelectedCellsText"), "copy");
                tab.FillDown();
                Equal(Text(top, "Value"), Text(host.Items[1], "Value"), "fill down");
                Unsorted();
            });
            Test("a table without a key and with a comma column saves by its fields only", () =>
            {
                using var other = new TestDatabase();
                other.Database.CreateTable("X", [TestDatabase.Text("Part Number"), TestDatabase.Text("Temp, Max")]);
                other.Insert("X", [new() { ["Part Number"] = "A", ["Temp, Max"] = "2" }, new() { ["Part Number"] = "B", ["Temp, Max"] = "1" }]);
                using var plain = new TabHost(other.Database, "X");
                Call(plain.Grid, "PerformSort", plain.Column("Temp, Max"));
                Wait(5);
                Equal("B", Text(plain.Items[0], "Part Number"), "sorted");
                Equal(null, plain.Tab.ChangeField(plain.Items[0].Row, "Temp, Max", "3"), "changed");
                True(plain.Tab.Save(plain.Window), "saved: " + string.Join(" ", Dialogs.TakeLast(1)));
                plain.CheckSameAsDatabase("keyless with a twin");
                plain.Tab.Discard();
            });
            Test("text sorts by value: Ohm, kOhm, MOhm and 4K7 codes", () =>
            {
                var descriptions = new[] { "RES 1 MOhm", "RES 4.75 kOhm", "RES 1 Ohm", "RES 10 Ohm", "RES 4.7 kOhm", "RES 1 kOhm", "RES 2 Ohm", "RES 9.76 Ohm", "RES 1.02 kOhm", "RES 100 kOhm", "RES 1.02 Ohm", "RES 0 Ohm" };
                var codes = new[] { "1M_0603", "4K75_0603", "1R_0603", "10R_0603", "4K7_0603", "1K_0603", "2R_0603", "9R76_0603", "1K02_0603", "100K_0603", "1R02_0603", "0R_0603" };
                var rows = host.Items.Select(v => v.Row).ToList();
                for (var i = 0; i < rows.Count; i++)
                {
                    Equal(null, tab.ChangeField(rows[i], "Description", descriptions[i]), "description");
                    Equal(null, tab.ChangeField(rows[i], "Temp, Max", codes[i]), "code");
                }
                Click("Description");
                InOrder(["RES 0 Ohm", "RES 1 Ohm", "RES 1.02 Ohm", "RES 2 Ohm", "RES 9.76 Ohm", "RES 10 Ohm", "RES 1 kOhm", "RES 1.02 kOhm", "RES 4.7 kOhm", "RES 4.75 kOhm", "RES 100 kOhm", "RES 1 MOhm"], "Description", "by value");
                Click("Temp, Max");
                InOrder(["0R_0603", "1R_0603", "1R02_0603", "2R_0603", "9R76_0603", "10R_0603", "1K_0603", "1K02_0603", "4K7_0603", "4K75_0603", "100K_0603", "1M_0603"], "Temp, Max", "codes, in a column with a comma");
                Click("Temp, Max");
                Equal("1M_0603", Shown("Temp, Max")[0], "down");
                Unsorted();
            });
            Test("an edited value moves to its place by value; undo puts it back; keys are never saved", () =>
            {
                Equal(null, tab.ChangeField(host.Item("PN-001").Row, "Description", "RES 2 Ohm"), "first");
                Equal(null, tab.ChangeField(host.Item("PN-002").Row, "Description", "RES 1 kOhm"), "second");
                Equal(null, tab.ChangeField(host.Item("PN-003").Row, "Description", "RES 10 Ohm"), "third");
                Click("Description");
                // The order of the three rows among the shown ones
                string Three() => string.Join(" ", host.Items.Select(v => Text(v, "Part Number")).Where(p => p is "PN-001" or "PN-002" or "PN-003"));
                Equal("PN-001 PN-003 PN-002", Three(), "sorted: 2 Ohm, 10 Ohm, 1 kOhm");
                TypeInCell(host, host.Item("PN-002"), "Description", "RES 5 Ohm");
                Equal("PN-001 PN-002 PN-003", Three(), "moved by its new value: 2, 5, 10 Ohm");
                True(tab.Save(host.Window), "saved");
                host.CheckSameAsDatabase("keys aren't fields");
                True(!tab.IsDirty, "nothing left to save");
                tab.Undo();
                Wait(5);
                True(tab.IsDirty, "the undo is a change");
                Equal("RES 1 kOhm", Text(host.Item("PN-002"), "Description"), "undone");
                Equal("PN-001 PN-003 PN-002", Three(), "back in its place");
                Unsorted();
                tab.SetTable(test.Database.LoadTable("T"), test.Database.GetTableFields("T"), test.Database);
                Wait();
                True(!tab.IsDirty, "a table read again has nothing to save");
            });
            Test("rows added and pasted get their keys", () =>
            {
                tab.AddRows([new Dictionary<string, string> { ["Part Number"] = "ADD-K", ["Description"] = "RES 3 Ohm" }]);
                Wait(5);
                Click("Description");
                if (host.Column("Description").SortDirection != ListSortDirection.Ascending)
                    Click("Description");
                var shown = Shown("Description");
                // The descriptions are "RES n Ohm" (n from 1 to 12, some changed by the test before), all in Ohms
                var values = shown.Select(d => double.Parse(d.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture)).ToList();
                True(shown.Contains("RES 3 Ohm") && values.SequenceEqual(values.Order()), "in order: " + string.Join(" | ", shown));
                tab.Undo();
                Wait(5);
                True(!tab.IsDirty, "dirty: " + Why(tab));
                Unsorted();
            });
            Test("the table read again sorts the same way", () =>
            {
                Click("Temp, Max");
                tab.SetTable(test.Database.LoadTable("T"), test.Database.GetTableFields("T"), test.Database);
                Wait();
                Equal(null, host.Column("Temp, Max").SortDirection, "arrow");
                Click("Temp, Max");
                Equal(ListSortDirection.Ascending, host.Column("Temp, Max").SortDirection, "sorts");
                Unsorted();
            });
        }
    }
}
