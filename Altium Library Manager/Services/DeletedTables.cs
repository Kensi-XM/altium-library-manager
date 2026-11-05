using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Altium_Library_Manager.Services.Templates;

namespace Altium_Library_Manager.Services
{
    /// <summary>A table deleted from a database, kept as a copy (see <see cref="DeletedTables"/>).</summary>
    public sealed record DeletedTable(string File, string Table, string Database, DateTime DeletedAt, int Rows,
        IReadOnlyList<TemplateField>? Fields)
    {
        public string DatabaseName => Database.Length == 0 ? "unknown database" : Path.GetFileName(Database);

        /// <summary>"from ComponentsDB.accdb · 217 rows · deleted 01.11.2026 14:24" (for the list).</summary>
        public string Summary => $"from {DatabaseName} · " +
                                 (Rows < 0 ? "" : Rows == 1 ? "1 row · " : $"{Rows:N0} rows · ") +
                                 $"deleted {DeletedAt:g}";

        // Screen readers read list items by their text
        public override string ToString() => Table;
    }

    /// <summary>
    /// The bin of deleted tables: before a table is deleted its copy is saved here as an .accdb file (the data), with a
    /// .json file next to it (the table's name, database, time, row count and original fields, so a restored table gets
    /// its key and AutoNumber back). Only the newest copies are kept. The .accdb can also be opened in Access directly.
    /// </summary>
    public static class DeletedTables
    {
        public static string Folder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Altium Library Manager", "Deleted tables");

        private const int Kept = 30;
        private const string FormatId = "altium-library-manager/deleted-table";
        private const long MaxInfoSize = 1024 * 1024;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
        };

        private sealed record Info(string? Format, string? Table, string? Database, DateTime DeletedAt, int Rows, List<FieldInfo>? Fields);

        private sealed record FieldInfo(string Name, FieldType Type, int Size, bool Required, bool PrimaryKey);

        /// <summary>Runs on a background thread: saves the copy of a table that is about to be deleted.</summary>
        public static DeletedTable Save(AccessDatabase database, string table)
        {
            Directory.CreateDirectory(Folder);
            var invalid = Path.GetInvalidFileNameChars();
            string Safe(string s) => new(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            var now = DateTime.Now;
            var name = $"{Safe(table)} ({Safe(Path.GetFileNameWithoutExtension(database.FilePath))}) {now:yyyy-MM-dd HH-mm-ss}";
            // Never onto an existing copy (two deletions within a second): the cleanup below would remove it
            var file = Path.Combine(Folder, name + ".accdb");
            for (var i = 2; File.Exists(file) || File.Exists(InfoPath(file)); i++)
                file = Path.Combine(Folder, $"{name} ({i}).accdb");

            var rows = database.CountRows(table);
            List<TemplateField> fields;
            try
            {
                fields = database.BackupTable(table, file);
                var info = new Info(FormatId, table, database.FilePath, now, rows,
                    fields.Select(f => new FieldInfo(f.Name, f.Type, f.Size, f.Required, f.PrimaryKey)).ToList());
                File.WriteAllText(InfoPath(file), JsonSerializer.Serialize(info, JsonOptions));
            }
            catch
            {
                // No half-made copy left behind
                TryDelete(file);
                TryDelete(InfoPath(file));
                throw;
            }

            // The copy is saved; trimming the oldest ones must not fail the deletion (a copy may be open in Access)
            foreach (var old in List().Skip(Kept))
            {
                TryDelete(old.File);
                TryDelete(InfoPath(old.File));
            }
            return new DeletedTable(file, table, database.FilePath, now, rows, fields);
        }

        /// <summary>The copies, newest first. A copy without its .json still shows (named after its file).</summary>
        public static List<DeletedTable> List()
        {
            if (!Directory.Exists(Folder))
                return [];
            var list = new List<DeletedTable>();
            foreach (var file in Directory.EnumerateFiles(Folder, "*.accdb"))
                list.Add(ReadInfo(file) ?? FromFileName(file));
            return list.OrderByDescending(t => t.DeletedAt).ToList();
        }

        // "<table> (<database>) yyyy-MM-dd HH-mm-ss.accdb"
        private static readonly System.Text.RegularExpressions.Regex FileNamePattern =
            new(@"^(?<table>.+) \((?<db>[^()]+)\) (?<time>\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2})$");

        /// <summary>A copy without its .json (made by an earlier version): what its file name tells.</summary>
        private static DeletedTable FromFileName(string file)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var match = FileNamePattern.Match(name);
            if (!match.Success)
                return new DeletedTable(file, name, "", File.GetLastWriteTime(file), -1, null);
            var time = DateTime.TryParseExact(match.Groups["time"].Value, "yyyy-MM-dd HH-mm-ss",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)
                ? t : File.GetLastWriteTime(file);
            return new DeletedTable(file, match.Groups["table"].Value, match.Groups["db"].Value + ".accdb", time, -1, null);
        }

        private static DeletedTable? ReadInfo(string file)
        {
            try
            {
                var path = InfoPath(file);
                if (!File.Exists(path) || new FileInfo(path).Length > MaxInfoSize)
                    return null;
                var info = JsonSerializer.Deserialize<Info>(File.ReadAllText(path), JsonOptions);
                // The name becomes a table name again on restore, so it must be a valid one
                if (info?.Format != FormatId || info.Table is not { } table || TemplateRules.CheckName(table, "Table") != null)
                    return null;
                var fields = info.Fields?.Select(f => new TemplateField
                {
                    Name = f.Name, Type = f.Type, Size = f.Size, Required = f.Required, PrimaryKey = f.PrimaryKey
                }).ToList();
                // Fields that don't pass the rules (a hand-edited file) are left out: the data's own fields are used then
                if (fields != null && TemplateRules.CheckFields(fields).Count > 0)
                    fields = null;
                return new DeletedTable(file, table, info.Database ?? "", info.DeletedAt, info.Rows, fields);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Runs on a background thread: puts the table back into <paramref name="database"/> as
        /// <paramref name="name"/>, then removes its copy from the bin.
        /// </summary>
        public static void Restore(DeletedTable table, AccessDatabase database, string name)
        {
            // The copy holds one table, named like the original (a copy without its .json may only know its file name)
            var tables = new AccessDatabase(table.File).GetTableNames();
            var source = tables.Contains(table.Table) ? table.Table
                : tables.FirstOrDefault() ?? throw new InvalidOperationException("The copy contains no table.");
            database.RestoreTable(table.File, source, name, table.Fields);
            // The table is back; if its copy can't be removed now (open in Access), it just stays in the bin
            TryDelete(table.File);
            TryDelete(InfoPath(table.File));
        }

        /// <summary>Removes the copy for good.</summary>
        public static void Delete(DeletedTable table)
        {
            File.Delete(table.File);
            TryDelete(InfoPath(table.File));
        }

        private static string InfoPath(string file) => Path.ChangeExtension(file, ".json");

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left over; harmless
            }
        }
    }
}
