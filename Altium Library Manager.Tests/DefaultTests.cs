using System.Data;
using System.Data.OleDb;
using System.Windows.Controls;
using Altium_Library_Manager.Services;
using static Altium_Library_Manager.Tests.T;
using static Altium_Library_Manager.Tests.TableTests;

namespace Altium_Library_Manager.Tests
{
    /// <summary>Default values of fields, as set in Access: a new row shows what will be saved.</summary>
    public static class DefaultTests
    {
        private static void Execute(AccessDatabase db, params string[] statements)
        {
            using var connection = new OleDbConnection($"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={db.FilePath}");
            connection.Open();
            foreach (var statement in statements)
            {
                using var command = new OleDbCommand(statement, connection);
                command.ExecuteNonQuery();
            }
        }

        public static void Run()
        {
            Section("Default values of fields");
            foreach (var (text, type, expected) in new (string, Type, object?)[]
                     {
                         ("\"abc\"", typeof(string), "abc"), ("'abc'", typeof(string), "abc"), ("\"say \"\"hi\"\"\"", typeof(string), "say \"hi\""), ("5", typeof(string), "5"),
                         ("Date()", typeof(string), null), ("5", typeof(int), 5), ("1.5", typeof(double), 1.5), ("abc", typeof(int), null), ("99999999999", typeof(int), null),
                         ("Yes", typeof(bool), true), ("No", typeof(bool), false), ("-1", typeof(bool), true), ("True", typeof(bool), true), ("Now()", typeof(DateTime), null), ("\"5\"", typeof(int), null)
                     })
                Test($"default [{text}] as {type.Name}", () => Equal(expected, AccessDatabase.ParseDefault(text, type), text));

            using var test = new TestDatabase();
            var db = test.Database;
            Execute(db, "CREATE TABLE T (ID COUNTER CONSTRAINT PK PRIMARY KEY, [Part Number] TEXT(50), [Maker] TEXT(50) DEFAULT 'ACME', " +
                        "[Qty] LONG DEFAULT 7, [Active] YESNO DEFAULT Yes, [Added] DATETIME DEFAULT Date(), [Note] TEXT(50))");
            test.Insert("T", [new() { ["Part Number"] = "A", ["Maker"] = "Other", ["Qty"] = 1, ["Active"] = false }]);
            using var host = new TabHost(db, "T");
            var tab = host.Tab;
            var grid = host.Grid;
            var table = tab.Table!;
            DataRow Saved(string part) => db.LoadTable("T").Rows.Cast<DataRow>().First(r => r["Part Number"] as string == part);

            Test("defaults are read with the table", () =>
            {
                Equal("ACME", table.Columns["Maker"]!.DefaultValue, "text");
                Equal(7, table.Columns["Qty"]!.DefaultValue, "number");
                Equal(true, table.Columns["Active"]!.DefaultValue, "yes");
                True(table.Columns["Added"]!.DefaultValue is DBNull, "a formula is left to Access");
            });
            Test("a row added by Add elements starts with them and saves them", () =>
            {
                tab.AddRows([new Dictionary<string, string> { ["Part Number"] = "B", ["Maker"] = "", ["Note"] = "n" }]);
                Wait(5);
                var row = host.Item("B");
                Equal("ACME", row["Maker"], "maker");
                Equal(7, row["Qty"], "qty");
                True(tab.Save(host.Window), "saved: " + string.Join(" ", Dialogs.TakeLast(1)));
                Equal("ACME", Saved("B")["Maker"], "in the file");
                Equal(true, Saved("B")["Active"], "yes in the file");
                True(Saved("B")["Added"] is DateTime, "Access filled in the formula");
            });
            Test("a row typed in the grid shows them at once", () =>
            {
                tab.AddRow(duplicate: false);
                Wait(5);
                var column = grid.CurrentColumn!;
                var item = (DataRowView)grid.CurrentItem;
                var container = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(item);
                Equal("ACME", item["Maker"], "maker in the new row");
                grid.ScrollIntoView(item, host.Column("Maker"));
                Wait(5);
                var cell = host.Column("Maker").GetCellContent(container);
                Equal("ACME", (cell as TextBox)?.Text ?? (cell as TextBlock)?.Text, "shown in its cell");
                ((TextBox)column.GetCellContent(container)).Text = "C";
                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                Wait(5);
                True(tab.Save(host.Window), "saved: " + string.Join(" ", Dialogs.TakeLast(1)));
                Equal("ACME", Saved("C")["Maker"], "in the file");
            });
            Test("a new row with only its defaults is dropped as untouched", () =>
            {
                var count = host.Items.Count;
                tab.AddRow(duplicate: false);
                Wait(5);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                Wait(5);
                Equal(count, host.Items.Count, "rows");
                True(!tab.IsDirty, "dirty: " + Why(tab));
            });
            Test("a default cleared in a new row stays empty in the file", () =>
            {
                tab.AddRows([new Dictionary<string, string> { ["Part Number"] = "D" }]);
                Wait(5);
                Equal(null, tab.ChangeField(host.Item("D").Row, "Maker", ""), "cleared");
                Equal(null, tab.ChangeField(host.Item("D").Row, "Qty", ""), "cleared");
                True(tab.Save(host.Window), "saved: " + string.Join(" ", Dialogs.TakeLast(1)));
                True(Saved("D").IsNull("Maker") && Saved("D").IsNull("Qty"), "empty");
            });
            Test("existing rows keep their own values", () =>
            {
                Equal("Other", host.Item("A")["Maker"], "maker");
                Equal(1, host.Item("A")["Qty"], "qty");
            });
            Test("a repeated row without a key keeps an empty field empty", () =>
            {
                const string add = "INSERT INTO K ([Part Number], [Maker]) VALUES ('A', NULL)";
                Execute(db, "CREATE TABLE K ([Part Number] TEXT(50), [Maker] TEXT(50) DEFAULT 'ACME')", add, add, add);
                var keyless = db.LoadTable("K");
                var row = keyless.Rows[0];
                var match = keyless.Columns.Cast<DataColumn>().Select(c => new FieldValue(c, row[c])).ToList();
                db.SaveChanges("K", [new RowWrite(RowWriteKind.Delete, "A", match, match, [])], null);
                var left = db.LoadTable("K").Rows.Cast<DataRow>().ToList();
                Equal(2, left.Count, "rows");
                True(left.All(r => r.IsNull("Maker")), "not filled with the default");
            });
            tab.Discard();
        }
    }
}
