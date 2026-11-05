using System.Collections.Concurrent;
using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using Altium_Library_Manager.Services.Templates;

namespace Altium_Library_Manager.Services
{
    /// <summary>A field of a row being written or looked for: its column (name and type) and value (DBNull for empty).</summary>
    public readonly record struct FieldValue(DataColumn Column, object Value);

    /// <summary>In the order they are written: deletions first, so a part number they free can be used again.</summary>
    public enum RowWriteKind
    {
        Delete,
        Update,
        Insert
    }

    /// <summary>
    /// One row of a table's changes being saved (see <see cref="AccessDatabase.SaveChanges"/>). <see cref="Match"/>
    /// finds the row to change or delete; <see cref="Whole"/>, its every value, is given when the table has no key, so
    /// one of several identical rows can still be told apart; <see cref="Values"/> are the changed fields, or all of a
    /// new row's. <see cref="Title"/> names the row in an error message.
    /// </summary>
    public sealed record RowWrite(RowWriteKind Kind, string Title, IReadOnlyList<FieldValue> Match,
        IReadOnlyList<FieldValue>? Whole, IReadOnlyList<FieldValue> Values);

    /// <summary>
    /// Reads the Access database (.mdb/.accdb) referenced by an Altium DbLib through the ACE OLE DB provider, adds and
    /// deletes tables (deleting only after the user confirmed it), and saves the rows that the user changed in a table
    /// tab.
    /// </summary>
    public class AccessDatabase
    {
        public string FilePath { get; }

        public AccessDatabase(string filePath)
        {
            FilePath = filePath;
        }

        // Built rather than formatted, so a path containing ';' or quotes stays one value
        private string ConnectionString => new OleDbConnectionStringBuilder
        {
            Provider = "Microsoft.ACE.OLEDB.12.0",
            DataSource = FilePath,
            PersistSecurityInfo = false
        }.ConnectionString;

        public IReadOnlyList<string> GetTableNames()
        {
            using var connection = new OleDbConnection(ConnectionString);
            connection.Open();

            var schema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, [null, null, null, "TABLE"]);
            return schema == null
                ? []
                : schema.Rows.Cast<DataRow>().Select(r => (string)r["TABLE_NAME"]).ToList();
        }

        public DataTable LoadTable(string tableName)
        {
            using var connection = new OleDbConnection(ConnectionString);
            using var adapter = new OleDbDataAdapter($"SELECT * FROM [{tableName.Replace("]", "]]")}]", connection);

            var table = new DataTable(tableName);
            connection.Open();
            adapter.Fill(table);
            ReadDefaults(connection, table);
            return table;
        }

        // The fields' default values, so that a new row starts with them and shows what will be saved. Only plain values:
        // a formula such as Date() is left to Access, which fills it in when the row is saved
        private static void ReadDefaults(OleDbConnection connection, DataTable table)
        {
            try
            {
                var schema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Columns, [null, null, table.TableName, null]);
                foreach (var row in schema?.Rows.Cast<DataRow>() ?? [])
                    if (row["COLUMN_HASDEFAULT"] is true && row["COLUMN_DEFAULT"] is string text && row["COLUMN_NAME"] is string name &&
                        table.Columns[name] is { } column && ParseDefault(text, column.DataType) is { } value)
                        column.DefaultValue = value;
            }
            catch (Exception ex) when (ex is OleDbException or ArgumentException or InvalidOperationException)
            {
                // Only a convenience: a table whose field details can't be read (a linked one) must still open
                System.Diagnostics.Debug.WriteLine($"Defaults of {table.TableName} not read: {ex.Message}");
            }
        }

        /// <summary>A default as Access stores it ("abc", 'abc', 5, 1.5, Yes) as a value of this type; null for anything else.</summary>
        public static object? ParseDefault(string text, Type type)
        {
            var value = text.Trim();
            var quoted = value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0];
            if (quoted)
                value = value[1..^1].Replace(new string(text.Trim()[0], 2), text.Trim()[0].ToString());
            if (type == typeof(string))
                return quoted || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? value : null;
            if (quoted || value.Length == 0)
                return null;
            if (type == typeof(bool))
                return value.ToLowerInvariant() switch
                {
                    "yes" or "true" or "on" or "-1" or "1" => true,
                    "no" or "false" or "off" or "0" => false,
                    _ => null
                };
            if (type == typeof(DateTime) || type == typeof(Guid) || type == typeof(byte[]))
                return null;
            try
            {
                var number = Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
                return number is double.NaN or double.PositiveInfinity or double.NegativeInfinity or float.NaN or float.PositiveInfinity or float.NegativeInfinity
                    ? null
                    : number;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
            {
                return null;
            }
        }

        private static string Quote(string name) => $"[{name.Replace("]", "]]")}]";

        /// <summary>What to tell the user when a database can't be opened: on a computer without the Access engine the
        /// provider's own message says nothing about what to do.</summary>
        public static string ExplainOpenError(Exception ex) =>
            ex is InvalidOperationException && ex.Message.Contains("not registered", StringComparison.OrdinalIgnoreCase)
                ? "The 64-bit Microsoft Access Database Engine isn't installed on this computer, and the app needs it to read Access files.\n\n" +
                  "Install \"Microsoft Access Database Engine 2016 Redistributable\" (accessdatabaseengine_X64.exe) from the Microsoft website, then open the database again."
                : ex.Message;

        /// <summary>
        /// Creates an empty table with the given fields (names checked with <see cref="TemplateRules"/> beforehand).
        /// One statement, so a failure leaves no half-made table behind.
        /// </summary>
        public void CreateTable(string tableName, IReadOnlyList<TemplateField> fields)
        {
            var columns = fields.Select(f =>
            {
                var type = f.Type switch
                {
                    FieldType.Text => $"TEXT({Math.Clamp(f.Size, 1, 255)})",
                    FieldType.LongText => "MEMO",
                    FieldType.Integer => "LONG",
                    FieldType.Number => "DOUBLE",
                    FieldType.Currency => "CURRENCY",
                    FieldType.YesNo => "YESNO",
                    FieldType.DateTime => "DATETIME",
                    FieldType.AutoNumber => "COUNTER",
                    _ => throw new ArgumentOutOfRangeException(nameof(fields))
                };
                var required = (f.Required || f.PrimaryKey) && f.Type is not (FieldType.AutoNumber or FieldType.YesNo);
                return $"{Quote(f.Name)} {type}{(required ? " NOT NULL" : "")}";
            }).ToList();

            var key = fields.Where(f => f.PrimaryKey).Select(f => Quote(f.Name)).ToList();
            if (key.Count > 0)
                columns.Add($"CONSTRAINT [PrimaryKey] PRIMARY KEY ({string.Join(", ", key)})");

            using var connection = new OleDbConnection(ConnectionString);
            connection.Open();
            using var command = new OleDbCommand($"CREATE TABLE {Quote(tableName)} ({string.Join(", ", columns)})", connection);
            command.ExecuteNonQuery();
        }

        public int CountRows(string tableName)
        {
            using var connection = new OleDbConnection(ConnectionString);
            connection.Open();
            using var command = new OleDbCommand($"SELECT COUNT(*) FROM {Quote(tableName)}", connection);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        /// <summary>
        /// Copies the table (its fields and all rows) into a new database file, so that a deleted table can be brought
        /// back. The copy holds only the data (no keys, AutoNumber values as plain numbers); the returned original
        /// fields let <see cref="RestoreTable"/> rebuild the table as it was.
        /// </summary>
        public List<TemplateField> BackupTable(string tableName, string backupPath)
        {
            var original = GetTableFields(tableName);
            CreateDatabaseFile(backupPath);
            var plain = original.Select(f => new TemplateField
            {
                Name = f.Name,
                Type = f.Type == FieldType.AutoNumber ? FieldType.Integer : f.Type,
                Size = f.Size
            }).ToList();
            CopyTableTo(new AccessDatabase(backupPath), tableName, tableName, plain);
            return original;
        }

        /// <summary>
        /// Adds a table copied from a backup file (see <see cref="BackupTable"/>) to this database under
        /// <paramref name="newName"/>, with the original fields (keys, AutoNumber) when they are known. All or nothing:
        /// if anything fails, the half-made table is removed again.
        /// </summary>
        public void RestoreTable(string backupPath, string backupTable, string newName, IReadOnlyList<TemplateField>? originalFields)
        {
            var backup = new AccessDatabase(backupPath);
            var fields = originalFields is { Count: > 0 } ? originalFields : backup.GetTableFields(backupTable);
            backup.CopyTableTo(this, backupTable, newName, fields);
        }

        /// <summary>
        /// Creates <paramref name="targetTable"/> in <paramref name="target"/> with the given fields (named as in the
        /// source table) and copies all rows into it in one transaction. (Access SQL can't copy into another file
        /// through this provider, hence row by row.) On failure the new table is dropped.
        /// </summary>
        private void CopyTableTo(AccessDatabase target, string sourceTable, string targetTable, IReadOnlyList<TemplateField> fields)
        {
            target.CreateTable(targetTable, fields);
            try
            {
                var columns = string.Join(", ", fields.Select(f => Quote(f.Name)));
                using var source = new OleDbConnection(ConnectionString);
                source.Open();
                using var select = new OleDbCommand($"SELECT {columns} FROM {Quote(sourceTable)}", source);
                using var reader = select.ExecuteReader();

                using var connection = new OleDbConnection(target.ConnectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction();
                var marks = string.Join(", ", fields.Select(_ => "?"));
                using var insert = new OleDbCommand($"INSERT INTO {Quote(targetTable)} ({columns}) VALUES ({marks})", connection, transaction);
                var values = new object[fields.Count];
                while (reader.Read())
                {
                    reader.GetValues(values);
                    insert.Parameters.Clear();
                    for (var i = 0; i < values.Length; i++)
                    {
                        // Binary values (rare in a component table) go into Long text as Base64; dates as Access dates
                        // (the default timestamp type with milliseconds is rejected as a type mismatch)
                        var parameter = insert.Parameters.AddWithValue($"@p{i}", values[i] is byte[] bytes ? Convert.ToBase64String(bytes) : values[i]);
                        if (values[i] is DateTime)
                            parameter.OleDbType = OleDbType.Date;
                    }
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            catch
            {
                try
                {
                    target.DeleteTable(targetTable);
                }
                catch (Exception)
                {
                    // The original error is the one worth reporting
                }
                throw;
            }
        }

        /// <summary>An empty .accdb file (through ADOX, which comes with the Access database engine).</summary>
        private static void CreateDatabaseFile(string path)
        {
            var type = Type.GetTypeFromProgID("ADOX.Catalog")
                       ?? throw new InvalidOperationException("ADOX isn't available to create a database file.");
            dynamic catalog = Activator.CreateInstance(type)!;
            try
            {
                var connection = catalog.Create(new OleDbConnectionStringBuilder
                {
                    Provider = "Microsoft.ACE.OLEDB.12.0",
                    DataSource = path
                }.ConnectionString);
                connection.Close();
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(connection);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(catalog);
            }
        }

        /// <summary>Deletes the table and all its rows (the caller asks the user first; this can't be undone).</summary>
        public void DeleteTable(string tableName)
        {
            using var connection = new OleDbConnection(ConnectionString);
            connection.Open();
            using var command = new OleDbCommand($"DROP TABLE {Quote(tableName)}", connection);
            command.ExecuteNonQuery();
        }

        #region Rows

        /// <summary>
        /// Writes a table's pending changes in one transaction, all or nothing: rows are deleted first, then changed, then
        /// added. A row to change or delete is found by <see cref="RowWrite.Match"/> (its key, or all its values when the
        /// table has no key) and must be there exactly once, otherwise nothing is saved. Returns the AutoNumber values
        /// Access gave the added rows, in their order (when <paramref name="autoNumber"/> is set).
        /// </summary>
        public List<object?> SaveChanges(string tableName, IReadOnlyList<RowWrite> writes, DataColumn? autoNumber)
        {
            var ids = new List<object?>();
            using var connection = new OleDbConnection(ConnectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            foreach (var write in writes.OrderBy(w => w.Kind))
            {
                try
                {
                    switch (write.Kind)
                    {
                        case RowWriteKind.Delete:
                            if (write.Whole != null && CountMatches(connection, transaction, tableName, write.Match) > 1)
                                ReplaceOneOfSame(connection, transaction, tableName, write.Match, write.Whole, changes: null);
                            else
                            {
                                using var delete = new OleDbCommand($"DELETE FROM {Quote(tableName)} WHERE {Where(write.Match)}", connection, transaction);
                                AddMatchParameters(delete, write.Match);
                                CheckOneRow(delete.ExecuteNonQuery());
                            }
                            break;

                        case RowWriteKind.Update when write.Values.Count > 0:
                            if (write.Whole != null && CountMatches(connection, transaction, tableName, write.Match) > 1)
                                ReplaceOneOfSame(connection, transaction, tableName, write.Match, write.Whole, write.Values);
                            else
                            {
                                var set = string.Join(", ", write.Values.Select(c => $"{Quote(c.Column.ColumnName)} = ?"));
                                using var update = new OleDbCommand($"UPDATE {Quote(tableName)} SET {set} WHERE {Where(write.Match)}", connection, transaction);
                                // Positional parameters: the new values first, then the ones the row is found by, as in the statement
                                foreach (var change in write.Values)
                                    AddParameter(update, change);
                                AddMatchParameters(update, write.Match);
                                CheckOneRow(update.ExecuteNonQuery());
                            }
                            break;

                        case RowWriteKind.Insert:
                            Insert(connection, transaction, tableName, write.Values);
                            if (autoNumber != null)
                            {
                                // The connection's last AutoNumber value: this row's
                                using var identity = new OleDbCommand("SELECT @@IDENTITY", connection, transaction);
                                ids.Add(identity.ExecuteScalar());
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"{write.Title}: {ex.Message}", ex);
                }
            }
            transaction.Commit();
            return ids;
        }

        // An empty field is left out, so Access fills in a default it computes itself. A field with a plain default started
        // with it, so empty there means cleared; rows put back as they were (keepEmpty) are written exactly too.
        private static void Insert(OleDbConnection connection, OleDbTransaction transaction, string tableName, IEnumerable<FieldValue> values,
            bool keepEmpty = false)
        {
            var all = values.ToList();
            var list = all.Where(v => v.Value is not DBNull || keepEmpty || v.Column.DefaultValue is not DBNull).ToList();
            if (all.All(v => v.Value is DBNull))
                throw new InvalidOperationException("The row is empty: fill in at least one field.");
            using var insert = new OleDbCommand(
                $"INSERT INTO {Quote(tableName)} ({string.Join(", ", list.Select(v => Quote(v.Column.ColumnName)))}) " +
                $"VALUES ({string.Join(", ", list.Select(_ => "?"))})", connection, transaction);
            foreach (var value in list)
                AddParameter(insert, value);
            insert.ExecuteNonQuery();
        }

        /// <summary>How many rows have this value in this field (text compared ignoring case, as Access does).</summary>
        public int CountRowsWith(string tableName, FieldValue value)
        {
            using var connection = new OleDbConnection(ConnectionString);
            connection.Open();
            using var count = new OleDbCommand($"SELECT COUNT(*) FROM {Quote(tableName)} WHERE {Where([value])}", connection);
            AddMatchParameters(count, [value]);
            return Convert.ToInt32(count.ExecuteScalar());
        }

        private static int CountMatches(OleDbConnection connection, OleDbTransaction transaction, string tableName,
            IReadOnlyList<FieldValue> match)
        {
            using var count = new OleDbCommand($"SELECT COUNT(*) FROM {Quote(tableName)} WHERE {Where(match)}", connection, transaction);
            AddMatchParameters(count, match);
            return Convert.ToInt32(count.ExecuteScalar());
        }

        /// <summary>
        /// A table without a key can hold the same row more than once (a duplicate saved twice). Access can't change or
        /// delete just one of them, but since they are alike, which one doesn't matter: all of them are deleted and put
        /// back, except one, which is left out (<paramref name="changes"/> null) or put back changed. The one taken is
        /// the one with every value of <paramref name="whole"/>, long texts included; if they differ in those, the
        /// rows aren't really the same and nothing is done. In the caller's transaction, so it is all or nothing.
        /// </summary>
        private static void ReplaceOneOfSame(OleDbConnection connection, OleDbTransaction transaction, string tableName,
            IReadOnlyList<FieldValue> match, IReadOnlyList<FieldValue> whole, IReadOnlyList<FieldValue>? changes)
        {
            var same = new DataTable();
            using (var select = new OleDbCommand($"SELECT * FROM {Quote(tableName)} WHERE {Where(match)}", connection, transaction))
            {
                AddMatchParameters(select, match);
                using var reader = select.ExecuteReader();
                same.Load(reader);
            }

            static bool Equal(object a, object b) => a is byte[] x && b is byte[] y ? x.AsSpan().SequenceEqual(y) : Equals(a, b);
            var taken = same.Rows.Cast<DataRow>().FirstOrDefault(r =>
                whole.All(f => same.Columns[f.Column.ColumnName] is { } c && Equal(r[c], f.Value)));
            if (taken == null)
                CheckOneRow(same.Rows.Count);   // differing only in long texts: can't tell which one is meant

            using (var delete = new OleDbCommand($"DELETE FROM {Quote(tableName)} WHERE {Where(match)}", connection, transaction))
            {
                AddMatchParameters(delete, match);
                if (delete.ExecuteNonQuery() != same.Rows.Count)
                    throw new InvalidOperationException("The rows changed while they were being saved. Reload the table (F5) and try again.");
            }

            foreach (var row in same.Rows.Cast<DataRow>().Where(r => r != taken))
                Insert(connection, transaction, tableName, same.Columns.Cast<DataColumn>().Select(c => new FieldValue(c, row[c])), keepEmpty: true);
            if (changes != null)
                Insert(connection, transaction, tableName, whole.Select(f => changes.FirstOrDefault(c => c.Column.ColumnName == f.Column.ColumnName) is { Column: not null } changed ? changed : f), keepEmpty: true);
        }

        // Rolling back (by disposing the transaction without committing) when the row isn't exactly one
        private static void CheckOneRow(int affected)
        {
            if (affected == 0)
                throw new InvalidOperationException(
                    "The row isn't in the database as shown any more: it was changed or deleted elsewhere (in Access or Altium). " +
                    "Reload the table (F5) to see its current contents.");
            if (affected > 1)
                throw new InvalidOperationException(
                    $"{affected} rows in the database have the same values except in long text fields, and the table has no primary key " +
                    "to tell them apart. Nothing was changed; edit these rows in Access.");
        }

        private static string Where(IReadOnlyList<FieldValue> match)
        {
            if (match.Count == 0)
                throw new InvalidOperationException("The row can't be found in the database: it has no fields to find it by.");
            return string.Join(" AND ", match.Select(m => m.Value is DBNull
                ? $"{Quote(m.Column.ColumnName)} IS NULL"
                : $"{Quote(m.Column.ColumnName)} = ?"));
        }

        private static void AddMatchParameters(OleDbCommand command, IReadOnlyList<FieldValue> match)
        {
            foreach (var m in match.Where(m => m.Value is not DBNull))
                AddParameter(command, m);
        }

        // Typed by the column, so an empty value (DBNull) has a type too, and dates are Access dates
        private static void AddParameter(OleDbCommand command, FieldValue field)
        {
            var type = field.Column.DataType;
            var oleDbType =
                type == typeof(string) ? (field.Value is string { Length: > 255 } ? OleDbType.LongVarWChar : OleDbType.VarWChar)
                : type == typeof(int) ? OleDbType.Integer
                : type == typeof(short) ? OleDbType.SmallInt
                : type == typeof(byte) ? OleDbType.UnsignedTinyInt
                : type == typeof(long) ? OleDbType.BigInt
                : type == typeof(double) ? OleDbType.Double
                : type == typeof(float) ? OleDbType.Single
                : type == typeof(decimal) ? OleDbType.Decimal
                : type == typeof(bool) ? OleDbType.Boolean
                : type == typeof(DateTime) ? OleDbType.Date
                : type == typeof(Guid) ? OleDbType.Guid
                : type == typeof(byte[]) ? OleDbType.LongVarBinary
                : OleDbType.Variant;
            var parameter = command.Parameters.Add($"@p{command.Parameters.Count}", oleDbType);
            parameter.Value = field.Value;
            // Currency and Decimal fields both read as decimal; as Currency a parameter would lose digits past the fourth
            if (oleDbType == OleDbType.Decimal)
                (parameter.Precision, parameter.Scale) = ((byte)28, field.Value is decimal number ? number.Scale : (byte)0);
        }

        #endregion

        /// <summary>The table's fields as a template (for "Save as template"): types, sizes, required and key.</summary>
        public List<TemplateField> GetTableFields(string tableName)
        {
            using var connection = new OleDbConnection(ConnectionString);
            connection.Open();
            using var command = new OleDbCommand($"SELECT * FROM {Quote(tableName)}", connection);
            using var reader = command.ExecuteReader(CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo);
            var schema = reader.GetSchemaTable();
            if (schema == null)
                return [];

            return schema.Rows.Cast<DataRow>().Select(r =>
            {
                var dataType = (Type)r["DataType"];
                var size = r["ColumnSize"] is int s ? s : TemplateField.DefaultTextSize;
                var isLong = r["IsLong"] is true;
                var key = r["IsKey"] is true;
                var type = r["IsAutoIncrement"] is true ? FieldType.AutoNumber
                    : dataType == typeof(Guid) ? FieldType.Text
                    : dataType == typeof(string) ? (isLong || size > 255 ? FieldType.LongText : FieldType.Text)
                    : dataType == typeof(bool) ? FieldType.YesNo
                    : dataType == typeof(DateTime) ? FieldType.DateTime
                    : dataType == typeof(decimal) ? FieldType.Currency
                    : dataType == typeof(double) || dataType == typeof(float) ? FieldType.Number
                    : dataType == typeof(int) || dataType == typeof(short) || dataType == typeof(byte) || dataType == typeof(long) ? FieldType.Integer
                    : FieldType.LongText;   // binary and other types: kept as text
                // A key can't be Long text (the template would be rejected); GUIDs are 38 characters as text
                if (key && type == FieldType.LongText)
                    type = FieldType.Text;
                if (dataType == typeof(Guid))
                    size = 38;
                return new TemplateField
                {
                    Name = (string)r["ColumnName"],
                    Type = type,
                    Size = type == FieldType.Text ? Math.Clamp(size, 1, 255) : TemplateField.DefaultTextSize,
                    Required = key || r["AllowDBNull"] is false,
                    PrimaryKey = key
                };
            }).ToList();
        }

        /// <summary>
        /// Finds a column by any of the given names, ignoring case and spaces ("Library Ref" == "libraryref").
        /// </summary>
        public static DataColumn? FindColumn(DataTable table, params string[] names)
        {
            static string Normalize(string s) => s.Replace(" ", "").Replace("_", "").ToLowerInvariant();

            var wanted = names.Select(Normalize).ToHashSet();
            return table.Columns.Cast<DataColumn>().FirstOrDefault(c => wanted.Contains(Normalize(c.ColumnName)));
        }

        /// <summary>
        /// Paths in the database are often relative or just a file name, so we also look for the file next to the database.
        /// </summary>
        public string? ResolveLibraryPath(string? libraryPath)
        {
            if (string.IsNullOrWhiteSpace(libraryPath))
                return null;

            // Searching subfolders is expensive, and hundreds of rows reference the same files
            return _resolvedPaths.GetOrAdd(libraryPath.Trim(), ResolveLibraryPathUncached);
        }

        private readonly ConcurrentDictionary<string, string?> _resolvedPaths = new(StringComparer.OrdinalIgnoreCase);

        private string? ResolveLibraryPathUncached(string libraryPath)
        {
            // A value that isn't a valid path (illegal characters, a typo in the database) just isn't found;
            // it must not throw on the preview's background thread
            try
            {
                if (Path.IsPathRooted(libraryPath) && File.Exists(libraryPath))
                    return libraryPath;

                var baseDir = Path.GetDirectoryName(FilePath)!;
                var combined = Path.GetFullPath(Path.Combine(baseDir, libraryPath));
                if (File.Exists(combined))
                    return combined;

                var fileName = Path.GetFileName(libraryPath);
                // Bounded: not into folder links (a junction pointing back up would loop forever) and not deeper than
                // a library tree goes, so a database in a large folder can't keep the search running for ages
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MaxRecursionDepth = 8,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
                };
                return Directory.EnumerateFiles(baseDir, fileName, options).FirstOrDefault();
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
