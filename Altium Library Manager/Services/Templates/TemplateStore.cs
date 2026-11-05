using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Altium_Library_Manager.Services.Templates
{
    /// <summary>
    /// Table templates, one JSON file each in %LOCALAPPDATA%\Altium Library Manager\Templates. A template file is
    /// self-contained, so exporting is copying it out and importing is reading one in; files can also be shared or
    /// edited by hand. Files that can't be read are skipped and reported, never deleted.
    /// </summary>
    public sealed class TemplateStore
    {
        /// <summary>Marks the file as a table template (and its layout version) when importing.</summary>
        private const string FormatId = "altium-library-manager/table-template";
        private const int FormatVersion = 1;

        private const long MaxFileSize = 1024 * 1024;
        private static readonly TimeSpan ReachTimeout = TimeSpan.FromSeconds(3);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Types by name only: a number would let any value through ("type": 99)
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
        };

        // The file layout, kept apart from the editable classes so the format doesn't change with the UI
        private sealed record TemplateFile(string? Format, int Version, string? Name, string? Description, List<FieldEntry>? Fields);

        private sealed record FieldEntry(string? Name, FieldType Type, int? Size, bool Required, bool PrimaryKey);

        // Each template's file and the name it was saved under (a rename moves the file)
        private readonly Dictionary<TableTemplate, (string Path, string Name)> _files = [];

        public TemplateStore(string? folder = null)
        {
            Folder = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : folder;
        }

        public static string DefaultFolder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Altium Library Manager", "Templates");

        public string Folder { get; }

        /// <summary>Sorted by name.</summary>
        public ObservableCollection<TableTemplate> Templates { get; } = [];

        /// <summary>Reads the folder again; returns the problems with files that were skipped.</summary>
        public List<string> Load()
        {
            var problems = new List<string>();
            Templates.Clear();
            _files.Clear();

            // A folder on a network share that is offline can take half a minute to answer; the templates are read on
            // the UI thread, so give up on it after a few seconds instead of freezing the window
            var exists = Task.Run(() => Directory.Exists(Folder));
            if (!exists.Wait(ReachTimeout))
            {
                problems.Add($"{Folder}: the folder doesn't respond (a network drive that is offline?)");
                return problems;
            }

            List<string> paths;
            try
            {
                // First start: begin with the standard Altium fields (the user may delete it)
                if (!exists.Result)
                {
                    Directory.CreateDirectory(Folder);
                    Add(BuiltInTemplates.AltiumComponent());
                    return problems;
                }
                paths = Directory.EnumerateFiles(Folder, "*.json").Order(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                problems.Add($"{Folder}: {ex.Message}");
                return problems;
            }

            foreach (var path in paths)
            {
                try
                {
                    var template = Read(path);
                    // Two files with the same template name (copied in by hand): keep both, told apart
                    var name = template.Name;
                    template.Name = UniqueName(name);
                    _files[template] = (path, name);
                    Insert(template);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
                {
                    problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
            return problems;
        }

        /// <summary>Saves a new template under a name no other template has (it joins the list only once saved).</summary>
        public void Add(TableTemplate template)
        {
            template.Name = UniqueName(template.Name);
            Save(template);
        }

        /// <summary>
        /// Writes the template to its file; a renamed template moves to a file with the new name. If writing fails
        /// nothing changes (the exception tells why).
        /// </summary>
        public void Save(TableTemplate template)
        {
            Directory.CreateDirectory(Folder);
            var known = _files.TryGetValue(template, out var old);
            var path = known && old.Name == template.Name ? old.Path : FreePath(template.Name, known ? old.Path : null);
            Write(template, path);
            _files[template] = (path, template.Name);
            if (known && !string.Equals(old.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(old.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The template is saved under its new name; the old file only shows up again after a restart
                }
            }

            // Keep the list sorted after a rename
            Templates.Remove(template);
            Insert(template);
        }

        public void Delete(TableTemplate template)
        {
            // The file first: if it can't be deleted, the template stays listed
            if (_files.TryGetValue(template, out var file))
                File.Delete(file.Path);
            _files.Remove(template);
            Templates.Remove(template);
        }

        /// <summary>A name not used by any other template: "Name", else "Name (2)", "Name (3)", …</summary>
        public string UniqueName(string name, TableTemplate? except = null)
        {
            name = string.IsNullOrWhiteSpace(name) ? "Template" : name.Trim();
            bool Taken(string candidate) => Templates.Any(t => t != except && string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase));
            if (!Taken(name))
                return name;
            for (var i = 2; ; i++)
                if (!Taken($"{name} ({i})"))
                    return $"{name} ({i})";
        }

        private void Insert(TableTemplate template)
        {
            var index = 0;
            while (index < Templates.Count && string.Compare(Templates[index].Name, template.Name, StringComparison.CurrentCultureIgnoreCase) < 0)
                index++;
            Templates.Insert(index, template);
        }

        internal static string FileNameFor(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var file = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            // Windows device names (CON, NUL, COM1, …) can't be file names, with or without an extension
            if (System.Text.RegularExpressions.Regex.IsMatch(file, @"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                file += "_";
            return file.Length == 0 ? "Template" : file;
        }

        /// <summary>A file name for the template that no other file has (its own file counts as free: renaming
        /// "resistor" to "Resistor" keeps the file).</summary>
        private string FreePath(string name, string? ownPath)
        {
            var file = FileNameFor(name);
            bool Taken(string path) => File.Exists(path) && !string.Equals(path, ownPath, StringComparison.OrdinalIgnoreCase);
            var path = Path.Combine(Folder, file + ".json");
            for (var i = 2; Taken(path); i++)
                path = Path.Combine(Folder, $"{file} ({i}).json");
            return path;
        }

        #region File format

        /// <summary>Reads and checks a template file; throws <see cref="FormatException"/> with the reason if it isn't one.</summary>
        public static TableTemplate Read(string path)
        {
            // A template is a few kilobytes; a huge file picked by mistake must not be read into memory
            if (new FileInfo(path).Length > MaxFileSize)
                throw new FormatException("too large for a table template");

            TemplateFile? file;
            try
            {
                file = JsonSerializer.Deserialize<TemplateFile>(File.ReadAllText(path), JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new FormatException($"not a valid JSON file ({ex.Message})", ex);
            }

            if (file == null || file.Format != FormatId)
                throw new FormatException("not a table template file");
            if (file.Version > FormatVersion)
                throw new FormatException("made by a newer version of the app");

            var template = new TableTemplate
            {
                Name = file.Name?.Trim() ?? "",
                Description = file.Description ?? "",
                Fields = new ObservableCollection<TemplateField>((file.Fields ?? []).Select(f => new TemplateField
                {
                    Name = f.Name?.Trim() ?? "",
                    Type = f.Type,
                    Size = f.Size ?? TemplateField.DefaultTextSize,
                    Required = f.Required || f.PrimaryKey,
                    PrimaryKey = f.PrimaryKey
                }))
            };
            if (template.Name.Length == 0)
                template.Name = Path.GetFileNameWithoutExtension(path);

            var errors = TemplateRules.CheckFields(template.Fields);
            if (errors.Count > 0)
                throw new FormatException(errors[0]);
            return template;
        }

        /// <summary>Writes the template file (also used for export); replaces the file only once fully written.</summary>
        public static void Write(TableTemplate template, string path)
        {
            var file = new TemplateFile(FormatId, FormatVersion, template.Name, template.Description,
                template.Fields.Select(f => new FieldEntry(f.Name, f.Type, f.HasSize ? f.Size : null, f.Required || f.PrimaryKey, f.PrimaryKey)).ToList());
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }

        #endregion
    }
}
