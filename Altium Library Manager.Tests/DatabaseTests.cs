using System.Data;
using Altium_Library_Manager.Services;
using static Altium_Library_Manager.Tests.T;

namespace Altium_Library_Manager.Tests
{
    /// <summary>Writing rows to a real Access file.</summary>
    public static class DatabaseTests
    {
        private static RowWrite Update(DataTable table, string partNumber, string column, object value, bool keyless = false)
        {
            var row = table.Rows.Cast<DataRow>().First(r => r["Part Number"] as string == partNumber);
            var match = keyless
                ? table.Columns.Cast<DataColumn>().Where(c => c.ColumnName != "Notes").Select(c => new FieldValue(c, row[c])).ToList()
                : [new FieldValue(table.Columns["ID"]!, row["ID"])];
            var whole = keyless ? table.Columns.Cast<DataColumn>().Select(c => new FieldValue(c, row[c])).ToList() : null;
            return new RowWrite(RowWriteKind.Update, partNumber, match, whole, [new FieldValue(table.Columns[column]!, value)]);
        }

        private static RowWrite Delete(DataTable table, string partNumber, bool keyless = false)
        {
            var update = Update(table, partNumber, "Part Number", "x", keyless);
            return update with { Kind = RowWriteKind.Delete, Values = [] };
        }

        private static string Cell(AccessDatabase database, string table, string partNumber, string column) =>
            Convert.ToString(database.LoadTable(table).Rows.Cast<DataRow>().First(r => r["Part Number"] as string == partNumber)[column]) ?? "";

        public static void Run()
        {
            Section("Saving rows");
            using var test = new TestDatabase();
            var db = test.Database;
            test.CreateKeyedTable("T", 5);
            test.CreateKeylessTable("K");

            Test("the schema is read: key, AutoNumber, types", () =>
            {
                var fields = db.GetTableFields("T");
                Equal("ID", fields.Single(f => f.PrimaryKey).Name, "key");
                Equal(Services.Templates.FieldType.AutoNumber, fields[0].Type, "auto");
                Equal(Services.Templates.FieldType.LongText, fields.First(f => f.Name == "Notes").Type, "memo");
                Equal(100, fields.First(f => f.Name == "Part Number").Size, "size");
            });
            foreach (var value in new[] { "O'Brien", "x'; DROP TABLE T; --", "[bracket]", "\"quoted\"", "100%_*?", "Кириллица µΩ", "tab\there", "{Value}", "a|b" })
                Test($"a value is stored as it is: {value}", () =>
                {
                    var table = db.LoadTable("T");
                    db.SaveChanges("T", [Update(table, "PN-001", "Description", value)], null);
                    Equal(value, Cell(db, "T", "PN-001", "Description"), "value");
                    True(db.GetTableNames().Contains("T"), "table still there");
                });
            Test("a long text over 255 characters", () =>
            {
                var text = new string('x', 5000);
                db.SaveChanges("T", [Update(db.LoadTable("T"), "PN-001", "Notes", text)], null);
                Equal(text, Cell(db, "T", "PN-001", "Notes"), "memo");
            });
            Test("every type of field", () =>
            {
                var date = new DateTime(2025, 3, 4, 5, 6, 7);
                var table = db.LoadTable("T");
                db.SaveChanges("T",
                [
                    Update(table, "PN-002", "Count", 42), Update(table, "PN-002", "Price", 1.5), Update(table, "PN-002", "Cost", 2.25m),
                    Update(table, "PN-002", "Active", true), Update(table, "PN-002", "Added", date)
                ], null);
                var row = db.LoadTable("T").Rows.Cast<DataRow>().First(r => r["Part Number"] as string == "PN-002");
                Equal(42, row["Count"], "int");
                Equal(1.5, row["Price"], "double");
                Equal(2.25m, row["Cost"], "currency");
                Equal(true, row["Active"], "bool");
                Equal(date, row["Added"], "date");
            });
            Test("clearing a value of each type", () =>
            {
                var table = db.LoadTable("T");
                db.SaveChanges("T", new[] { "Count", "Price", "Cost", "Added", "Description", "Notes" }.Select(c => Update(table, "PN-002", c, DBNull.Value)).ToList(), null);
                var row = db.LoadTable("T").Rows.Cast<DataRow>().First(r => r["Part Number"] as string == "PN-002");
                foreach (var column in new[] { "Count", "Price", "Cost", "Added", "Description", "Notes" })
                    True(row.IsNull(column), column);
            });
            Test("a field name with special characters", () =>
            {
                db.SaveChanges("T", [Update(db.LoadTable("T"), "PN-003", "Size / Dimension", "new")], null);
                Equal("new", Cell(db, "T", "PN-003", "Size / Dimension"), "slash");
                db.SaveChanges("T", [Update(db.LoadTable("T"), "PN-003", "Height - Seated (Max)", "h")], null);
                db.SaveChanges("T", [Update(db.LoadTable("T"), "PN-003", "Lifetime @ Temp", "l")], null);
                Equal("l", Cell(db, "T", "PN-003", "Lifetime @ Temp"), "at");
            });
            Test("added rows get their AutoNumber, in order", () =>
            {
                var table = db.LoadTable("T");
                var part = table.Columns["Part Number"]!;
                var ids = db.SaveChanges("T",
                [
                    new RowWrite(RowWriteKind.Insert, "a", [], null, [new FieldValue(part, "NEW-1")]),
                    new RowWrite(RowWriteKind.Insert, "b", [], null, [new FieldValue(part, "NEW-2")])
                ], table.Columns["ID"]);
                Equal(2, ids.Count, "ids");
                Equal(Cell(db, "T", "NEW-1", "ID"), Convert.ToString(ids[0]), "first");
                Equal(Cell(db, "T", "NEW-2", "ID"), Convert.ToString(ids[1]), "second");
            });
            Test("all or nothing: a missing row undoes the rest", () =>
            {
                var table = db.LoadTable("T");
                var before = TabHost.Contents(table);
                var missing = new RowWrite(RowWriteKind.Update, "gone", [new FieldValue(table.Columns["ID"]!, 99999)], null, [new FieldValue(table.Columns["Value"]!, "x")]);
                Throws<InvalidOperationException>(() => db.SaveChanges("T", [Update(table, "PN-004", "Value", "changed"), missing], null), "missing row");
                Equal(string.Join("\n", before), string.Join("\n", TabHost.Contents(db.LoadTable("T"))), "contents");
            });
            Test("all or nothing: a refused insert undoes the deletions", () =>
            {
                var table = db.LoadTable("T");
                var count = table.Rows.Count;
                var tooLong = new RowWrite(RowWriteKind.Insert, "long", [], null, [new FieldValue(table.Columns["Value"]!, new string('x', 200))]);
                Throws<InvalidOperationException>(() => db.SaveChanges("T", [Delete(table, "PN-005"), tooLong], null), "too long");
                Equal(count, db.CountRows("T"), "rows");
            });
            Test("the error names the row", () =>
            {
                var table = db.LoadTable("T");
                var missing = new RowWrite(RowWriteKind.Delete, "THE ROW", [new FieldValue(table.Columns["ID"]!, 99999)], null, []);
                try
                {
                    db.SaveChanges("T", [missing], null);
                }
                catch (InvalidOperationException ex)
                {
                    True(ex.Message.StartsWith("THE ROW"), ex.Message);
                }
            });
            Test("deletions come before additions (a part number can be reused)", () =>
            {
                var table = db.LoadTable("T");
                var part = table.Columns["Part Number"]!;
                db.SaveChanges("T", [new RowWrite(RowWriteKind.Insert, "n", [], null, [new FieldValue(part, "REUSED")]), Delete(table, "NEW-1")], null);
                Equal(0, db.LoadTable("T").Rows.Cast<DataRow>().Count(r => r["Part Number"] as string == "NEW-1"), "deleted");
                Equal(1, db.LoadTable("T").Rows.Cast<DataRow>().Count(r => r["Part Number"] as string == "REUSED"), "added");
            });
            Test("an empty new row is refused", () =>
                Throws<InvalidOperationException>(() => db.SaveChanges("T", [new RowWrite(RowWriteKind.Insert, "e", [], null, [])], null), "empty"));
            Test("a part number is counted ignoring case", () =>
            {
                var table = db.LoadTable("T");
                Equal(1, db.CountRowsWith("T", new FieldValue(table.Columns["Part Number"]!, "pn-001")), "case");
                Equal(0, db.CountRowsWith("T", new FieldValue(table.Columns["Part Number"]!, "PN-0%")), "no wildcards");
            });

            Test("a Decimal field keeps all its digits", () =>
            {
                using (var connection = new System.Data.OleDb.OleDbConnection($"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={db.FilePath}"))
                {
                    connection.Open();
                    using var create = new System.Data.OleDb.OleDbCommand("CREATE TABLE D ([Part Number] TEXT(50), [Amount] DECIMAL(18,6))", connection);
                    create.ExecuteNonQuery();
                }
                test.Insert("D", [new() { ["Part Number"] = "A", ["Amount"] = 1.5m }]);
                var table = db.LoadTable("D");
                var row = table.Rows[0];
                var match = table.Columns.Cast<DataColumn>().Select(c => new FieldValue(c, row[c])).ToList();
                db.SaveChanges("D", [new RowWrite(RowWriteKind.Update, "A", match, match, [new FieldValue(table.Columns["Amount"]!, 0.123456m)])], null);
                Equal(0.123456m, db.LoadTable("D").Rows[0]["Amount"], "digits");
            });
            Test("a table deleted to a backup comes back the same", () =>
            {
                var memo = new string('m', 3000);
                test.Insert("T", [new() { ["Part Number"] = "BK", ["Notes"] = memo, ["Added"] = new DateTime(2025, 3, 4, 5, 6, 7), ["Cost"] = 1.2345m, ["Price"] = 0.1, ["Active"] = true }]);
                var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(db.FilePath)!, "backup.accdb");
                var fields = db.BackupTable("T", path);
                db.RestoreTable(path, "T", "T restored", fields);
                Equal(string.Join("\n", TabHost.Contents(db.LoadTable("T"), null)), string.Join("\n", TabHost.Contents(db.LoadTable("T restored"), null)), "contents");
                Equal("ID", db.GetTableFields("T restored").Single(f => f.PrimaryKey).Name, "key");
                db.DeleteTable("T restored");
            });

            Section("Tables without a key");
            Test("deleting one of two identical rows leaves the other", () =>
            {
                db.SaveChanges("K", [Delete(db.LoadTable("K"), "A", keyless: true)], null);
                Equal(1, db.LoadTable("K").Rows.Cast<DataRow>().Count(r => r["Part Number"] as string == "A"), "left");
            });
            Test("changing one of three identical rows leaves the others", () =>
            {
                db.SaveChanges("K", [Update(db.LoadTable("K"), "C", "Description", "changed", keyless: true)], null);
                var rows = db.LoadTable("K").Rows.Cast<DataRow>().Where(r => r["Part Number"] as string == "C").ToList();
                Equal(3, rows.Count, "rows");
                Equal(1, rows.Count(r => r["Description"] as string == "changed"), "changed");
            });
            Test("two changes to identical rows in one save", () =>
            {
                var table = db.LoadTable("K");
                var a = Update(table, "C", "Value", "2R", keyless: true);
                db.SaveChanges("K", [Update(db.LoadTable("K"), "D", "Value", "9R", keyless: true), a], null);
                Equal("9R", Cell(db, "K", "D", "Value"), "D");
            });
            Test("rows differing only in a long text aren't mixed up", () =>
            {
                test.Insert("K", [new() { ["Part Number"] = "M", ["Notes"] = "one" }, new() { ["Part Number"] = "M", ["Notes"] = "two" }]);
                var table = db.LoadTable("K");
                var row = table.Rows.Cast<DataRow>().First(r => r["Part Number"] as string == "M" && r["Notes"] as string == "two");
                var match = table.Columns.Cast<DataColumn>().Where(c => c.ColumnName != "Notes").Select(c => new FieldValue(c, row[c])).ToList();
                var whole = table.Columns.Cast<DataColumn>().Select(c => new FieldValue(c, row[c])).ToList();
                db.SaveChanges("K", [new RowWrite(RowWriteKind.Delete, "M", match, whole, [])], null);
                var left = db.LoadTable("K").Rows.Cast<DataRow>().Where(r => r["Part Number"] as string == "M").ToList();
                Equal(1, left.Count, "left");
                Equal("one", left[0]["Notes"], "the right one went");
            });
        }
    }
}
