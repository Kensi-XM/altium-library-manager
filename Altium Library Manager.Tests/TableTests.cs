using System.Data;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Altium_Library_Manager.Services;
using static Altium_Library_Manager.Tests.T;

namespace Altium_Library_Manager.Tests
{
    /// <summary>A table tab in a window: selecting, editing, history, saving; and the Add elements window.</summary>
    public static class TableTests
    {
        /// <summary>The messages the app would have shown.</summary>
        public static readonly List<string> Dialogs = [];

        // Never on the screen: the tests run while the user works, and random actions are refused all the time
        public static void AnswerDialogs() => AppDialog.Instead = (title, message, detail, buttons) =>
        {
            Dialogs.Add($"{title} / {message} / {detail}");
            return Math.Max(0, buttons.Length - 1);
        };

        public static IEnumerable<V> All<V>(DependencyObject root) where V : DependencyObject
        {
            if (root is V found)
                yield return found;
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in All<V>(System.Windows.Media.VisualTreeHelper.GetChild(root, i)))
                    yield return child;
        }

        internal static string Why(TableTab tab) => string.Join("; ", tab.Table!.Rows.Cast<DataRow>().Where(r => r.RowState != DataRowState.Unchanged).Take(3).Select(r =>
            r.RowState + " " + (r.RowState == DataRowState.Deleted ? "" : string.Join(",", tab.Table.Columns.Cast<DataColumn>()
                .Where(c => r.RowState == DataRowState.Modified && !Equals(r[c, DataRowVersion.Original], r[c, DataRowVersion.Current]))
                .Select(c => $"{c.ColumnName}:[{r[c, DataRowVersion.Original]}]->[{r[c, DataRowVersion.Current]}]")))));

        internal static string Text(DataRowView row, string column) => Convert.ToString(row[column]) ?? "";

        // A cell edited the way the grid does it: into edit, typed, left
        internal static void TypeInCell(TabHost host, DataRowView item, string column, string text, bool endRow = true)
        {
            var col = host.Column(column);
            host.Tab.SelectItem(item, col);
            host.Grid.ScrollIntoView(item, col);
            Wait();
            host.Grid.CurrentCell = new DataGridCellInfo(item, col);
            host.Grid.BeginEdit();
            Wait();
            var box = (TextBox)col.GetCellContent((DataGridRow)host.Grid.ItemContainerGenerator.ContainerFromItem(item));
            box.Text = text;
            host.Grid.CommitEdit(DataGridEditingUnit.Cell, true);
            if (endRow)
                host.Grid.CommitEdit(DataGridEditingUnit.Row, true);
            Wait();
        }

        public static void Run()
        {
            using var test = new TestDatabase();
            test.CreateKeyedTable("T", 12);
            using var host = new TabHost(test.Database, "T");
            var tab = host.Tab;
            var grid = host.Grid;
            void Reset()
            {
                tab.Discard();
                tab.Query = "";
                tab.ApplySearchNow();
                Dialogs.Clear();
                Wait(5);
            }

            Section("Columns with special characters in their names");
            foreach (var column in new[] { "Size / Dimension", "Lifetime @ Temp", "Height - Seated (Max)", "Temp, Max", "Tol %", "Part Number" })
                Test($"[{column}] shows its value", () =>
                {
                    var item = host.Items[0];
                    grid.ScrollIntoView(item, host.Column(column));
                    Wait();
                    var cell = (TextBlock)host.Column(column).GetCellContent((DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(item));
                    Equal(Text(item, column), cell.Text, column);
                });
            foreach (var column in new[] { "Size / Dimension", "Value" })
                Test($"[{column}] shows a change made elsewhere, and its undo, at once", () =>
                {
                    var item = host.Items[0];
                    var before = Text(item, column);
                    grid.ScrollIntoView(item, host.Column(column));
                    Wait();
                    var cell = (TextBlock)host.Column(column).GetCellContent((DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(item));
                    Equal(null, tab.ChangeField(item.Row, column, "changed"), "change");
                    Wait();
                    Equal("changed", cell.Text, "changed");
                    tab.Undo();
                    Wait();
                    Equal(before, cell.Text, "undone");
                    Reset();
                });
            Test("[Size / Dimension] can be edited in its cell", () =>
            {
                TypeInCell(host, host.Items[0], "Size / Dimension", "edited");
                Equal("edited", Text(host.Items[0], "Size / Dimension"), "value");
                Reset();
            });

            Test("[Temp, Max] can be edited", () =>
            {
                TypeInCell(host, host.Items[0], "Temp, Max", "125");
                Equal("125", Text(host.Items[0], "Temp, Max"), "value");
                Reset();
            });
            Test("[Size / Dimension] can be sorted and searched", () =>
            {
                grid.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription(host.Column("Size / Dimension").SortMemberPath, System.ComponentModel.ListSortDirection.Descending));
                Wait();
                Equal("9 mm", Text(host.Items[0], "Size / Dimension"), "sorted");
                grid.Items.SortDescriptions.Clear();
                tab.Query = "7 mm";
                tab.ApplySearchNow();
                Wait();
                True(host.Items.Count < 12 && host.Items.Any(v => Text(v, "Size / Dimension") == "7 mm"), "found");
                Reset();
            });

            foreach (var column in new[] { "Size / Dimension", "Temp, Max", "Value" })
                Test($"[{column}]: a cell left, then the row: the change is kept, saved and undone", () =>
                {
                    var item = host.Items[1];
                    var before = Text(item, column);
                    TypeInCell(host, item, column, "typed", endRow: false);
                    Wait(60);
                    Equal("typed", Text(item, column), "in the row");
                    grid.CommitEdit(DataGridEditingUnit.Row, true);
                    Wait(60);
                    True(tab.IsDirty, "the change counts: " + tab.Table!.Rows[1].RowState);
                    True(tab.Save(host.Window), "saved");
                    host.CheckSameAsDatabase("after saving");
                    tab.Undo();
                    Wait(5);
                    Equal(before, Text(host.Items[1], column), "undone");
                    True(tab.Save(host.Window), "saved back");
                    host.CheckSameAsDatabase("after saving the undo");
                    Reset();
                });
            Test("[Size / Dimension]: Esc after leaving the cell undoes it", () =>
            {
                var item = host.Items[1];
                var before = Text(item, "Size / Dimension");
                TypeInCell(host, item, "Size / Dimension", "typed", endRow: false);
                Wait(60);
                grid.CancelEdit(DataGridEditingUnit.Row);
                Wait(5);
                Equal(before, Text(item, "Size / Dimension"), "value");
                True(!tab.IsDirty, "dirty: " + Why(tab));
                Reset();
            });
            Test("reading the table again with a cell selected, then sorting", () =>
            {
                tab.SelectItem(host.Items[3], host.Column("Value"));
                tab.SetTable(test.Database.LoadTable("T"), test.Database.GetTableFields("T"), test.Database);
                Wait(5);
                Equal(0, grid.SelectedCells.Count, "nothing selected");
                Equal(null, tab.SelectedRow, "no row");
                tab.ShowField("Description");
                Call(grid, "PerformSort", host.Column("Value"));
                Wait(5);
                grid.Items.SortDescriptions.Clear();
                foreach (var c in grid.Columns)
                    c.SortDirection = null;
                Reset();
            });

            Section("Selection");
            Test("the selected row follows the current cell", () =>
            {
                tab.SelectItem(host.Items[3], host.Column("Value"));
                Equal(host.Items[3].Row, tab.SelectedRow?.Row, "row");
                Equal(1, grid.SelectedCells.Count, "one cell");
                Equal(tab.SelectedRow, grid.Tag, "the row's shade");
            });
            Test("selecting nothing", () =>
            {
                tab.SelectItem(null);
                Equal(null, tab.SelectedRow, "none");
                Equal(0, grid.SelectedCells.Count, "cells");
            });
            Test("the row changes once, not per cell", () =>
            {
                var changes = 0;
                void Count() => changes++;
                tab.SelectItem(host.Items[0], host.Column("Value"));
                tab.SelectedRowChanged += Count;
                tab.SelectItem(host.Items[0], host.Column("Description"));
                Equal(0, changes, "same row");
                tab.SelectItem(host.Items[1], host.Column("Description"));
                Equal(1, changes, "other row");
                tab.SelectedRowChanged -= Count;
            });
            Test("Select row twice, Select column twice", () =>
            {
                host.Select("Value", host.Items[1], host.Items[2]);
                tab.SelectWholeRows();
                tab.SelectWholeRows();
                Equal(2 * grid.Columns.Count, grid.SelectedCells.Count, "rows");
                tab.SelectColumn(host.Column("Value"));
                tab.SelectColumn(host.Column("Value"));
                Equal(host.Items.Count, grid.SelectedCells.Count, "column");
            });
            Test("Select rows with the whole table selected", () =>
            {
                grid.SelectAllCells();
                tab.SelectWholeRows();
                Equal(grid.Items.Count * grid.Columns.Count, grid.SelectedCells.Count, "all");
            });
            Test("the rows of selected cells are counted once", () =>
            {
                host.Select("Value", host.Items[1], host.Items[2]);
                grid.SelectedCells.Add(new DataGridCellInfo(host.Items[1], host.Column("Description")));
                Equal(2, tab.SelectedRows().Count, "rows");
            });
            Test("a field picked in the details selects its cell", () =>
            {
                tab.SelectItem(host.Items[2], host.Column("Value"));
                tab.ShowField("Description");
                Equal("Description", grid.CurrentColumn?.Header, "column");
                Equal(host.Items[2].Row, tab.SelectedRow?.Row, "row");
                tab.ShowField("no such field");
                Equal("Description", grid.CurrentColumn?.Header, "unchanged");
            });

            Section("Changing cells");
            Test("nothing changes with Edit off", () =>
            {
                tab.IsEditing = false;
                host.Select("Value", host.Items[0]);
                tab.ClearSelectedCells();
                Call(tab, "SetSelectedCells", "Fill", "x");
                tab.FillDown();
                tab.Paste();
                tab.IsEditing = true;
                True(!tab.IsDirty, "dirty: " + Why(tab));
            });
            Test("clear, undo, redo", () =>
            {
                var before = Text(host.Items[0], "Value");
                host.Select("Value", host.Items[0], host.Items[1]);
                tab.ClearSelectedCells();
                Equal("", Text(host.Items[0], "Value"), "cleared");
                True(tab.IsDirty, "dirty");
                tab.Undo();
                Equal(before, Text(host.Items[0], "Value"), "undone");
                True(!tab.IsDirty, "clean again: " + Why(tab));
                tab.Redo();
                Equal("", Text(host.Items[1], "Value"), "redone");
                Reset();
            });
            Test("clearing a number, a date, Yes/No", () =>
            {
                foreach (var column in new[] { "Count", "Price", "Cost", "Added" })
                {
                    host.Select(column, host.Items[0]);
                    tab.ClearSelectedCells();
                    True(host.Items[0].Row.IsNull(column), column);
                }
                Equal(0, Dialogs.Count, string.Join(";", Dialogs));
                Reset();
            });
            Test("a cleared Yes/No field is No (Access can't leave it empty)", () =>
            {
                var item = host.Items[1];
                True((bool)item["Active"], "starts as Yes");
                host.Select("Active", item);
                tab.ClearSelectedCells();
                Equal(false, item["Active"], "cleared");
                True(tab.Save(host.Window), "saved");
                host.CheckSameAsDatabase("after clearing a Yes/No field");
                tab.Undo();
                True(tab.Save(host.Window), "saved back");
                Reset();
            });
            Test("a new row left untouched is dropped", () =>
            {
                var count = host.Items.Count;
                tab.AddRow(duplicate: false);
                Wait();
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                Wait();
                Equal(count, host.Items.Count, "rows");
                True(!tab.IsDirty, "dirty: " + Why(tab));
                Reset();
            });
            foreach (var text in new[] { "NaN", "Infinity", "-Infinity", "1e999" })
                Test($"a number field refuses [{text}]", () =>
                {
                    host.Select("Price", host.Items[0]);
                    Call(tab, "SetSelectedCells", "Fill", text);
                    Wait(5);
                    True(!tab.IsDirty, "dirty: " + Why(tab));
                    Equal(1, Dialogs.Count, "reported");
                    Reset();
                });
            Test("a refused paste says nothing was pasted, not that part of it was", () =>
            {
                string? status = null;
                void Note(string s) => status = s;
                tab.Status += Note;
                host.Select("Part Number", host.Items[0]);
                Call(tab, "PasteText", string.Join("\t", Enumerable.Repeat("PN-002", grid.Columns.Count + 2)));
                Wait(5);
                tab.Status -= Note;
                True(status?.StartsWith("Pasted") != true, $"status: {status}");
                True(!tab.IsDirty, "dirty: " + Why(tab));
                Reset();
            });
            Test("filling a number field: good and bad", () =>
            {
                host.Select("Count", host.Items[0], host.Items[1]);
                Call(tab, "SetSelectedCells", "Fill", "7");
                Equal(7, host.Items[1]["Count"], "number");
                Call(tab, "SetSelectedCells", "Fill", "seven");
                Wait(120);
                Equal(1, Dialogs.Count, "refused");
                Equal(7, host.Items[1]["Count"], "kept");
                Reset();
            });
            Test("filling mixed columns with a value one can't take changes nothing", () =>
            {
                host.Select("Value", host.Items[0]);
                grid.SelectedCells.Add(new DataGridCellInfo(host.Items[0], host.Column("Count")));
                Call(tab, "SetSelectedCells", "Fill", "text");
                Wait(120);
                True(!tab.IsDirty, "dirty: " + Why(tab));
                Reset();
            });
            Test("a value too long for its field changes nothing", () =>
            {
                host.Select("Value", host.Items[0], host.Items[1]);
                Call(tab, "SetSelectedCells", "Fill", new string('x', 60));
                Wait(120);
                True(!tab.IsDirty, "dirty: " + Why(tab));
                Equal(1, Dialogs.Count, "reported");
                Reset();
            });
            Test("AutoNumber cells are left alone", () =>
            {
                var id = host.Items[0]["ID"];
                host.Select("ID", host.Items[0]);
                tab.ClearSelectedCells();
                Call(tab, "SetSelectedCells", "Fill", "999");
                Equal(id, host.Items[0]["ID"], "id");
                True(!tab.IsDirty, "dirty: " + Why(tab));
                Reset();
            });
            Test("fill down: per column, in the order shown", () =>
            {
                host.Select("Value", host.Items[0], host.Items[1], host.Items[2]);
                grid.SelectedCells.Add(new DataGridCellInfo(host.Items[0], host.Column("Description")));
                grid.SelectedCells.Add(new DataGridCellInfo(host.Items[1], host.Column("Description")));
                var (value, description) = (Text(host.Items[0], "Value"), Text(host.Items[0], "Description"));
                tab.FillDown();
                Equal(value, Text(host.Items[2], "Value"), "value");
                Equal(description, Text(host.Items[1], "Description"), "description");
                Reset();
            });
            Test("fill down with a sort follows the rows on screen", () =>
            {
                grid.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription("Count", System.ComponentModel.ListSortDirection.Descending));
                Wait();
                var top = host.Items[0];
                host.Select("Value", host.Items[0], host.Items[1]);
                tab.FillDown();
                Equal(Text(top, "Value"), Text(host.Items[1], "Value"), "from the top on screen");
                grid.Items.SortDescriptions.Clear();
                Reset();
            });
            Test("paste a block", () =>
            {
                host.Select("Description", host.Items[1]);
                var next = grid.Columns.First(c => c.DisplayIndex == host.Column("Description").DisplayIndex + 1);
                Call(tab, "PasteText", "A1\tB1\r\nA2\tB2\r\n");
                Equal("A1", Text(host.Items[1], "Description"), "A1");
                Equal("A2", Text(host.Items[2], "Description"), "A2");
                if (next.IsReadOnly)
                    Equal(2, ((string?)tab.UndoDescription)?.Contains('2') == true ? 2 : 0, "read-only column left");
                else
                    Equal("B2", Text(host.Items[2], (string)next.Header), "B2");
                Reset();
            });
            Test("paste one value into many cells", () =>
            {
                host.Select("Description", host.Items[1], host.Items[3]);
                Call(tab, "PasteText", "ONE\r\n");
                Equal("ONE", Text(host.Items[3], "Description"), "value");
                Reset();
            });
            Test("paste past the end of the table", () =>
            {
                host.Select("Description", host.Items[^1]);
                Call(tab, "PasteText", "a\nb\nc");
                Equal("a", Text(host.Items[^1], "Description"), "first");
                Reset();
                var last = grid.Columns.OrderBy(c => c.DisplayIndex).Last();
                host.Select((string)last.Header, host.Items[0]);
                Call(tab, "PasteText", "1\t2\t3\n4\t5\t6");
                Reset();
            });
            Test("paste bad text into a number field changes nothing", () =>
            {
                host.Select("Count", host.Items[0], host.Items[1]);
                Call(tab, "PasteText", "1\nnot a number");
                Wait(120);
                True(!tab.IsDirty, "dirty: " + Why(tab));
                Reset();
            });
            Test("paste with nothing selected, or nothing to paste", () =>
            {
                tab.SelectItem(null);
                Call(tab, "PasteText", "x");
                host.Select("Value", host.Items[0]);
                Call(tab, "PasteText", "");
                True(!tab.IsDirty, "dirty: " + Why(tab));
            });
            Test("paste too much text", () =>
            {
                host.Select("Notes", host.Items[0]);
                Call(tab, "PasteText", new string('x', 50_000_001));
                True(!tab.IsDirty, "dirty: " + Why(tab));
            });
            Test("copy: rows and columns in the order shown", () =>
            {
                host.Select("Value", host.Items[2], host.Items[0]);
                grid.SelectedCells.Add(new DataGridCellInfo(host.Items[0], host.Column("Description")));
                var expected = $"{Text(host.Items[0], "Description")}\t{Text(host.Items[0], "Value")}\r\n\t{Text(host.Items[2], "Value")}\r\n";
                Equal(expected, Call(tab, "SelectedCellsText"), "text");
            });
            Test("copy keeps a value in one cell", () =>
            {
                host.Items[0].Row["Description"] = "a\tb\r\nc";
                host.Select("Description", host.Items[0]);
                Equal("a b c\r\n", Call(tab, "SelectedCellsText"), "text");
                Reset();
            });
            Test("copy and paste back gives the same table", () =>
            {
                host.Select("Description", host.Items[0], host.Items[1]);
                var text = (string)Call(tab, "SelectedCellsText")!;
                Call(tab, "PasteText", text);
                True(!tab.IsDirty, "nothing changed");
                Reset();
            });

            Section("Part numbers");
            Test("the same part number on two rows is refused", () =>
            {
                host.Select("Part Number", host.Items[0], host.Items[1]);
                Call(tab, "SetSelectedCells", "Fill", "SAME");
                Wait(120);
                True(!tab.IsDirty, "dirty: " + Why(tab));
                Reset();
            });
            Test("a part number another row has is refused, any case", () =>
            {
                host.Select("Part Number", host.Items[0]);
                Call(tab, "SetSelectedCells", "Fill", "pn-002");
                Wait(120);
                True(!tab.IsDirty, "dirty: " + Why(tab));
                Reset();
            });
            Test("clearing part numbers is refused", () =>
            {
                host.Select("Part Number", host.Items[0]);
                tab.ClearSelectedCells();
                Wait(120);
                True(!tab.IsDirty, "dirty: " + Why(tab));
                Reset();
            });
            Test("a list of new part numbers goes in", () =>
            {
                host.Select("Part Number", host.Items[0]);
                Call(tab, "PasteText", "N1\nN2\nN3");
                Equal("N3", Text(host.Items[2], "Part Number"), "third");
                Reset();
            });
            Test("swapping two part numbers by paste is refused, nothing lost", () =>
            {
                host.Select("Part Number", host.Items[0]);
                Call(tab, "PasteText", "PN-002\nPN-001");
                Wait(120);
                Equal("PN-001", Text(host.Items[0], "Part Number"), "kept");
                Reset();
            });
            Test("a row's own part number in another case is allowed", () =>
            {
                var error = tab.ChangeField(host.Items[0].Row, "Part Number", "pn-001");
                Equal(null, error, "error");
                Reset();
            });
            Test("another row's part number that was deleted can be used", () =>
            {
                var free = host.Items[1];
                tab.SelectItem(free);
                host.Select("Value", free);
                tab.DeleteSelectedRows();
                Wait();
                Equal(null, tab.ChangeField(host.Items[0].Row, "Part Number", "PN-002"), "free");
                Reset();
            });

            Section("Rows in the grid");
            Test("an edit reaches the row when the cell is left", () =>
            {
                var item = host.Items[0];
                TypeInCell(host, item, "Description", "typed", endRow: false);
                Equal("typed", Text(item, "Description"), "value");
                True(!tab.IsDirty, "not a change until the row is left");
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                Wait();
                True(tab.IsDirty, "changed");
                tab.Undo();
                True(!tab.IsDirty, "undone");
                Reset();
            });
            Test("Esc undoes the row's cells", () =>
            {
                var item = host.Items[0];
                var before = Text(item, "Description");
                TypeInCell(host, item, "Description", "typed", endRow: false);
                grid.CancelEdit(DataGridEditingUnit.Row);
                Wait();
                Equal(before, Text(item, "Description"), "value");
                True(!tab.IsDirty, "dirty: " + Why(tab));
                Reset();
            });
            Test("undo right after Enter takes back that edit", () =>
            {
                var item = host.Items[0];
                var before = Text(item, "Description");
                var col = host.Column("Description");
                tab.SelectItem(item, col);
                grid.ScrollIntoView(item, col);
                Wait();
                grid.CurrentCell = new DataGridCellInfo(item, col);
                grid.BeginEdit();
                Wait();
                ((TextBox)col.GetCellContent((DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(item))).Text = "typed";
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                tab.Undo();
                Equal(before, Text(host.Items[0], "Description"), "value");
                Reset();
            });
            Test("clearing cells while a row is being edited ends it first", () =>
            {
                var item = host.Items[0];
                TypeInCell(host, item, "Description", "typed", endRow: false);
                host.Select("Value", host.Items[1]);
                tab.ClearSelectedCells();
                Wait();
                Equal("typed", Text(host.Items[0], "Description"), "kept");
                Reset();
            });
            Test("duplicate: numbered, and its copy has all the fields", () =>
            {
                var source = host.Items[1];
                tab.SelectItem(source, grid.Columns.OrderBy(c => c.DisplayIndex).First(c => !c.IsReadOnly));
                tab.AddRow(duplicate: true);
                Wait();
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                Wait();
                var copy = host.Item("PN-002 (2)");
                Equal(Text(source, "Description"), Text(copy, "Description"), "description");
                Equal(Text(source, "Notes"), Text(copy, "Notes"), "long text");
                Equal(source["Count"], copy["Count"], "number");
                tab.Undo();
                True(!host.Items.Any(v => v["Part Number"] as string == "PN-002 (2)"), "undone");
                Reset();
            });
            Test("Duplicate while the selected row is a new, empty one", () =>
            {
                var count = host.Items.Count;
                tab.AddRow(duplicate: false);
                Wait(5);
                tab.AddRow(duplicate: true);
                Wait(5);
                Equal(count, host.Items.Count, "nothing added");
                True(!tab.IsDirty, "dirty: " + Why(tab));
                Reset();
            });
            Test("deleting the last row selects the row above it", () =>
            {
                var above = host.Items[^2].Row;
                host.Select("Value", host.Items[^1]);
                tab.DeleteSelectedRows();
                Wait(5);
                Equal(above, tab.SelectedRow?.Row, "selected");
                Reset();
            });
            Test("delete rows, then undo restores them with their values", () =>
            {
                var before = TabHost.Contents(tab.Table!);
                host.Select("Value", host.Items[1], host.Items[3]);
                tab.DeleteSelectedRows();
                Wait();
                Equal(before.Count - 2, host.Items.Count, "rows");
                True(tab.SelectedRow != null, "a row stays selected");
                tab.Undo();
                Wait();
                Equal(string.Join("\n", before), string.Join("\n", TabHost.Contents(tab.Table!)), "contents");
                True(!tab.IsDirty, "nothing to save");
                Reset();
            });
            Test("deleting every row, then undo", () =>
            {
                grid.SelectAllCells();
                tab.DeleteSelectedRows();
                Wait();
                Equal(0, host.Items.Count, "empty");
                Equal(null, tab.SelectedRow, "nothing selected");
                tab.Undo();
                Wait();
                Equal(12, host.Items.Count, "back");
                Reset();
            });
            Test("search hides rows; a change to a hidden row's cell stays", () =>
            {
                tab.Query = "PN-003";
                tab.ApplySearchNow();
                Wait();
                Equal(1, host.Items.Count, "found");
                host.Select("Value", host.Items[0]);
                Call(tab, "SetSelectedCells", "Fill", "x");
                tab.Query = "";
                tab.ApplySearchNow();
                Wait();
                Equal("x", Text(host.Item("PN-003"), "Value"), "value");
                Reset();
            });
            Test("search stays right after an edit (its text is read again)", () =>
            {
                host.Select("Value", host.Items[0]);
                Call(tab, "SetSelectedCells", "Fill", "UNIQUEWORD");
                tab.Query = "UNIQUEWORD";
                tab.ApplySearchNow();
                Wait();
                Equal(1, host.Items.Count, "found");
                tab.Query = "";
                tab.ApplySearchNow();
                tab.Undo();
                tab.Query = "UNIQUEWORD";
                tab.ApplySearchNow();
                Wait();
                Equal(0, host.Items.Count, "gone after undo");
                Reset();
            });

            Section("Saving");
            Test("a save writes everything, once", () =>
            {
                host.Select("Value", host.Items[0]);
                Call(tab, "SetSelectedCells", "Fill", "SAVED");
                tab.AddRows([new Dictionary<string, string> { ["Part Number"] = "ADDED", ["Count"] = "5", ["Value"] = "" }]);
                host.Select("Value", host.Items[2]);
                tab.DeleteSelectedRows();
                Wait();
                True(tab.Save(host.Window), "saved");
                True(!tab.IsDirty, "clean");
                host.CheckSameAsDatabase("after save");
                Equal(true, host.Item("ADDED")["ID"] is int, "AutoNumber arrived");
            });
            Test("undo past a save, and save again", () =>
            {
                tab.Undo();
                tab.Undo();
                tab.Undo();
                True(tab.Save(host.Window), "saved");
                host.CheckSameAsDatabase("after second save");
                Equal(12, host.Items.Count, "rows");
            });
            Test("a row changed in the database meanwhile is reported, nothing lost", () =>
            {
                var item = host.Items[0];
                var other = test.Database.LoadTable("T");
                var id = item["ID"];
                test.Database.SaveChanges("T", [new RowWrite(RowWriteKind.Delete, "x", [new FieldValue(other.Columns["ID"]!, id)], null, [])], null);
                host.Select("Value", item);
                Call(tab, "SetSelectedCells", "Fill", "LATE");
                var saved = tab.Save(host.Window);
                Wait(120);
                True(!saved, "not saved");
                True(tab.IsDirty, "changes kept");
                Equal(1, Dialogs.Count, "reported");
                Reset();
            });

            Section("History limits");
            Test("a huge history is cut, the last step stays", () =>
            {
                using var large = new TestDatabase();
                large.CreateKeylessTable("L");
                large.Insert("L", Enumerable.Range(0, 3000).Select(i => new Dictionary<string, object> { ["Part Number"] = $"P{i}", ["Value"] = "v" }));
                using var big = new TabHost(large.Database, "L");
                big.Tab.SelectColumn(big.Column("Value"));
                for (var i = 0; i < 40; i++)
                    Call(big.Tab, "SetSelectedCells", "Fill", $"v{i}");
                var undo = (System.Collections.ICollection)typeof(TableTab).GetField("_undo", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(big.Tab)!;
                True(undo.Count < 40, $"kept {undo.Count} steps");
                True(undo.Count >= 30, $"only {undo.Count} steps");
                big.Tab.Undo();
                Equal("v38", big.Items[0]["Value"], "undo still works");
                big.Tab.Discard();
            });

            Section("Add elements");
            var types = ElementTypes.Load([]);
            AddElementsWindow Open()
            {
                var dialog = new AddElementsWindow(tab, test.Database, new AltiumRenderService()) { ShowActivated = false, Left = -3000 };
                dialog.Show();
                Wait();
                return dialog;
            }
            V Part<V>(AddElementsWindow d, string name) => (V)d.FindName(name);
            List<ElementField> Fields(AddElementsWindow d) => (List<ElementField>)typeof(AddElementsWindow).GetField("_fields", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(d)!;
            List<ElementRow> Preview(AddElementsWindow d) => ((IEnumerable<ElementRow>)Part<DataGrid>(d, "PreviewGrid").ItemsSource).ToList();
            void Pick(AddElementsWindow d, string name) => Part<ComboBox>(d, "KindCombo").SelectedItem = Part<ComboBox>(d, "KindCombo").Items.OfType<ElementType>().First(t => t.Name == name);

            Test("opens for every type, with and without a selected row", () =>
            {
                foreach (var selected in new[] { true, false })
                {
                    tab.SelectItem(selected ? host.Items[0] : null);
                    var dialog = Open();
                    foreach (var type in types)
                    {
                        Pick(dialog, type.Name);
                        Wait(2);
                    }
                    dialog.Close();
                }
            });
            Test("opens for a table with no part number field", () =>
            {
                using var other = new TestDatabase();
                other.Database.CreateTable("X", [TestDatabase.Text("Name")]);
                other.Insert("X", [new() { ["Name"] = "a" }]);
                using var plain = new TabHost(other.Database, "X");
                var dialog = new AddElementsWindow(plain.Tab, other.Database, new AltiumRenderService()) { ShowActivated = false, Left = -3000 };
                dialog.Show();
                Wait();
                dialog.Close();
            });
            Test("rows made, part numbers checked, added in one step", () =>
            {
                tab.SelectItem(host.Items[0]);
                var dialog = Open();
                Pick(dialog, "Resistor");
                Fields(dialog).First(f => f.Name == "Part Number").Pattern = "NEW {Value}";
                Part<RadioButton>(dialog, "ListRadio").IsChecked = true;
                Part<TextBox>(dialog, "ValuesList").Text = "100, 4K7, 100R";
                Wait();
                var rows = Preview(dialog);
                Equal(3, rows.Count, "preview");
                Equal("Same as a row above", rows[2].Note, "repeat left out");
                Equal("Add 2 rows", Part<Button>(dialog, "AddButton").Content, "button");
                var count = host.Items.Count;
                Call(dialog, "Add_Click", null, null);
                Wait();
                Equal(count + 2, host.Items.Count, "added");
                Equal("Add 2 rows", tab.UndoDescription, "one step");
                tab.Undo();
                Equal(count, host.Items.Count, "undone");
                Reset();
            });
            Test("a field that can't take the text stops the button", () =>
            {
                tab.SelectItem(host.Items[0]);
                var dialog = Open();
                Pick(dialog, "Resistor");
                Part<RadioButton>(dialog, "ListRadio").IsChecked = true;
                Part<TextBox>(dialog, "ValuesList").Text = "100, 4K7";
                Fields(dialog).First(f => f.Name == "Part Number").Pattern = "NEW {Value}";
                Fields(dialog).First(f => f.Name == "Count").Pattern = "not a number";
                Wait();
                True(!Part<Button>(dialog, "AddButton").IsEnabled, "disabled");
                True(((string)Part<TextBlock>(dialog, "ErrorText").Text).Length > 0, "explained");
                dialog.Close();
            });
            Test("an endless range in From / To", () =>
            {
                var dialog = Open();
                Pick(dialog, "Resistor");
                Part<RadioButton>(dialog, "SeriesRadio").IsChecked = true;
                Part<TextBox>(dialog, "ToBox").Text = new string('9', 400);
                Wait();
                True(!Part<Button>(dialog, "AddButton").IsEnabled, "no rows from an endless range");
                dialog.Close();
            });
            Test("the widest series stays quick to type in", () =>
            {
                var dialog = Open();
                Pick(dialog, "Resistor");
                Part<RadioButton>(dialog, "SeriesRadio").IsChecked = true;
                Part<ComboBox>(dialog, "SeriesCombo").SelectedItem = ElementValues.AllSeries.First(s => s.Name == "E192");
                Part<TextBox>(dialog, "FromBox").Text = "1";
                var clock = Stopwatch.StartNew();
                Part<TextBox>(dialog, "ToBox").Text = "1G";
                var time = clock.ElapsedMilliseconds;
                Console.WriteLine($"    (E192, 1 to 1G: {Preview(dialog).Count} rows in {time} ms)");
                True(time < 1500, $"took {time} ms");
                dialog.Close();
            });
            Reset();
        }
    }
}
