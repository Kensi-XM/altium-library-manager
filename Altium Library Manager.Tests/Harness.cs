using System.Data;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Altium_Library_Manager.Services;
using Altium_Library_Manager.Services.Settings;
using Altium_Library_Manager.Services.Templates;

namespace Altium_Library_Manager.Tests
{
    /// <summary>Failed check.</summary>
    public sealed class CheckFailed(string message) : Exception(message);

    /// <summary>Small test runner: the app's tests need a WPF thread, a real database and no test framework.</summary>
    public static class T
    {
        private static int _passed;
        private static string _running = "";
        private static DateTime _started = DateTime.UtcNow;

        /// <summary>A hanging test ends the run, naming the test, instead of leaving it stuck.</summary>
        public static void WatchForHangs(int seconds) => new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(1000);
                if ((DateTime.UtcNow - _started).TotalSeconds > seconds)
                {
                    Console.WriteLine($"  HUNG {_running}");
                    Environment.Exit(3);
                }
            }
        }) { IsBackground = true }.Start();
        private static readonly List<string> Failures = [];

        public static void Section(string name) => Console.WriteLine($"\n== {name}");

        public static void Test(string name, Action test)
        {
            _running = name;
            _started = DateTime.UtcNow;
            RunTest(name, test);
        }

        private static void RunTest(string name, Action test)
        {
            try
            {
                test();
                _passed++;
            }
            catch (Exception ex)
            {
                var problem = (ex is TargetInvocationException { InnerException: { } inner } ? inner : ex) is var real && real is CheckFailed
                    ? real.Message
                    : real.ToString().Split('\n').Take(6).Aggregate((a, b) => a + "\n      " + b.Trim());
                Failures.Add($"{name}: {problem}");
                Console.WriteLine($"  FAIL {name}\n      {problem}");
            }
        }

        public static void Equal<V>(V expected, V actual, string? what = null)
        {
            if (!EqualityComparer<V>.Default.Equals(expected, actual))
                throw new CheckFailed($"{what}: expected [{expected}], got [{actual}]");
        }

        public static void True(bool value, string what)
        {
            if (!value)
                throw new CheckFailed(what);
        }

        public static void Near(double expected, double? actual, string what)
        {
            if (actual is not { } value || Math.Abs(value - expected) > Math.Abs(expected) * 1e-9)
                throw new CheckFailed($"{what}: expected {expected}, got {actual}");
        }

        public static void Throws<E>(Action action, string what) where E : Exception
        {
            try
            {
                action();
            }
            catch (E)
            {
                return;
            }
            throw new CheckFailed($"{what}: nothing was thrown");
        }

        public static int Report()
        {
            Console.WriteLine($"\n{_passed} passed, {Failures.Count} failed");
            foreach (var failure in Failures)
                Console.WriteLine($"  FAILED {failure.Split('\n')[0]}");
            return Failures.Count == 0 ? 0 : 1;
        }

        /// <summary>Lets the dispatcher run, as the app does between user actions.</summary>
        public static void Wait(int ms = 30)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        public static object? Call(object target, string method, params object?[] args) =>
            target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(target, args);
    }

    /// <summary>A database of its own in the temp folder, with tables for the tests.</summary>
    public sealed class TestDatabase : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "alm-tests", Guid.NewGuid().ToString("N"));

        public AccessDatabase Database { get; }

        public TestDatabase()
        {
            Directory.CreateDirectory(_folder);
            var path = Path.Combine(_folder, "test.accdb");
            typeof(AccessDatabase).GetMethod("CreateDatabaseFile", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [path]);
            Database = new AccessDatabase(path);
        }

        public string Folder => _folder;

        public static TemplateField Text(string name, int size = 100, bool key = false, bool required = false) =>
            new() { Name = name, Type = FieldType.Text, Size = size, PrimaryKey = key, Required = required || key };

        public static TemplateField Field(string name, FieldType type, bool key = false) => new() { Name = name, Type = type, PrimaryKey = key };

        /// <summary>A table like the Altium ones: AutoNumber key, text fields, and one of each other type, with awkward names.</summary>
        public void CreateKeyedTable(string name, int rows)
        {
            Database.CreateTable(name,
            [
                Field("ID", FieldType.AutoNumber, key: true), Text("Part Number"), Text("Library Ref"), Text("Library Path", 255), Text("Footprint Ref"),
                Text("Footprint Path", 255), Text("Footprint Ref 2"), Text("Description", 255), Text("Value", 50), Text("Size / Dimension"),
                Text("Lifetime @ Temp"), Text("Height - Seated (Max)"), Text("Temp, Max"), Text("Tol %"), Field("Qty, Min", FieldType.Integer), Field("Notes", FieldType.LongText), Field("Count", FieldType.Integer),
                Field("Price", FieldType.Number), Field("Cost", FieldType.Currency), Field("Active", FieldType.YesNo), Field("Added", FieldType.DateTime)
            ]);
            Insert(name, Enumerable.Range(1, rows).Select(i => new Dictionary<string, object>
            {
                ["Part Number"] = $"PN-{i:000}", ["Library Ref"] = "Resistor", ["Library Path"] = @"SCH\R.SchLib", ["Footprint Ref"] = "FP", ["Footprint Path"] = @"PCB\R.PcbLib",
                ["Description"] = $"RES {i} Ohm", ["Value"] = $"{i}R", ["Size / Dimension"] = $"{i} mm", ["Lifetime @ Temp"] = "1000h", ["Height - Seated (Max)"] = "1 mm", ["Temp, Max"] = $"T{i * 5 % 12:00}", ["Tol %"] = "5", ["Qty, Min"] = i * 7 % 12 + 1,
                ["Notes"] = new string('n', 300) + i, ["Count"] = i, ["Price"] = i / 8.0, ["Cost"] = i * 1.25m, ["Active"] = i % 2 == 0, ["Added"] = new DateTime(2024, 1, 1).AddDays(i)
            }));
        }

        /// <summary>A table without a key, holding some rows twice.</summary>
        public void CreateKeylessTable(string name)
        {
            Database.CreateTable(name, [Text("Part Number"), Text("Description"), Text("Value", 50), Field("Notes", FieldType.LongText)]);
            Insert(name, new[] { "A", "A", "B", "C", "C", "C", "D" }.Select(p => new Dictionary<string, object>
            {
                ["Part Number"] = p, ["Description"] = "same", ["Value"] = "1R", ["Notes"] = "note"
            }));
        }

        public void Insert(string table, IEnumerable<Dictionary<string, object>> rows)
        {
            var columns = Database.LoadTable(table).Columns;
            Database.SaveChanges(table, rows.Select(r => new RowWrite(RowWriteKind.Insert, "seed", [], null,
                r.Select(p => new FieldValue(columns[p.Key]!, p.Value)).ToList())).ToList(), null);
        }

        public void Dispose()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left in the temp folder
            }
        }
    }

    /// <summary>A table tab in a window of its own (the grid has to be laid out to behave as in the app).</summary>
    public sealed class TabHost : IDisposable
    {
        public TableTab Tab { get; }

        public Window Window { get; }

        public AccessDatabase Database { get; }

        public string Table { get; }

        public TabHost(AccessDatabase database, string table)
        {
            Database = database;
            Table = table;
            Tab = new TableTab(table);
            Window = new Window { Content = Tab.View, Width = 1400, Height = 700, Left = -3000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            Window.Show();
            Tab.SetTable(database.LoadTable(table), database.GetTableFields(table), database);
            T.Wait();
            Tab.IsEditing = true;
            T.Wait();
        }

        public DataGrid Grid => Tab.Grid;

        public DataGridColumn Column(string name) => Grid.Columns.First(c => string.Equals((string)c.Header, name, StringComparison.OrdinalIgnoreCase));

        public List<DataRowView> Items => Grid.Items.OfType<DataRowView>().ToList();

        public DataRowView Item(string partNumber) => Items.First(v => v["Part Number"] as string == partNumber);

        /// <summary>Selects cells of a column, as a drag would.</summary>
        public void Select(string column, params DataRowView[] rows)
        {
            Tab.SelectItem(rows[0], Column(column));
            foreach (var row in rows.Skip(1))
                Grid.SelectedCells.Add(new DataGridCellInfo(row, Column(column)));
            T.Wait(5);
        }

        /// <summary>What the database holds now: every row's values, as sorted text.</summary>
        public static List<string> Contents(DataTable table, string? skip = "ID") => table.Rows.Cast<DataRow>()
            .Where(r => r.RowState != DataRowState.Deleted)
            .Select(r => string.Join("|", TableTab.FieldColumns(table).Where(c => c.ColumnName != skip).Select(c => Convert.ToString(r[c], System.Globalization.CultureInfo.InvariantCulture))))
            .Order(StringComparer.Ordinal).ToList();

        public void CheckSameAsDatabase(string when)
        {
            var (table, database) = (Contents(Tab.Table!), Contents(Database.LoadTable(Table)));
            T.Equal(database.Count, table.Count, $"{when}: rows");
            for (var i = 0; i < table.Count; i++)
                if (table[i] != database[i])
                    throw new CheckFailed($"{when}: row differs\n      table: {table[i]}\n      db:    {database[i]}");
        }

        public void Dispose() => Window.Close();
    }
}
