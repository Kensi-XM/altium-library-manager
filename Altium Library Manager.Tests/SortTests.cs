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
                True(grid.Columns.All(c => c.Header is string name && tab.Table.Columns[name]!.Expression.Length == 0), "headers");
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
