using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Altium_Library_Manager.Services.Templates;

namespace Altium_Library_Manager.Services
{
    /// <summary>
    /// A type of element for the Add elements window: how its values are written, what to offer, and, for a saved set,
    /// the values and field texts it was saved with. One JSON file each.
    /// </summary>
    public sealed class ElementType
    {
        public string Name { get; set; } = "";

        /// <summary>Written after the value: Ohm, F, H, V, A, Hz. Empty for plain text values.</summary>
        public string Unit { get; set; } = "";

        public ElementStyle Style { get; set; } = ElementStyle.Suffix;

        /// <summary>With <see cref="ElementStyle.Letter"/>: the letter of the plain unit (R in 0R56, V in 3V3).</summary>
        public string CodeLetter { get; set; } = "";

        /// <summary>The prefixes values are written with (p, n, u, m, k, M, G; empty for none).</summary>
        public List<string> Prefixes { get; set; } = [""];

        /// <summary>The prefix a bare number has: "p" makes 100 mean 100 pF.</summary>
        public string Bare { get; set; } = "";

        /// <summary>The series offered first (see <see cref="ElementValues.AllSeries"/>).</summary>
        public string Series { get; set; } = "E24";

        public string From { get; set; } = "";

        public string To { get; set; } = "";

        /// <summary>Values of a typed list. When set, the list is what the type starts with.</summary>
        public string Values { get; set; } = "";

        /// <summary>The fields that usually hold the value, the likeliest first.</summary>
        public List<string> ValueFields { get; set; } = ["Value"];

        /// <summary>Words in a table's name that suggest this type; the longest match wins.</summary>
        public List<string> TableWords { get; set; } = [];

        /// <summary>Words for when no type's <see cref="TableWords"/> match: "capacitor" means ceramic unless the name
        /// says tantalum.</summary>
        public List<string> GeneralWords { get; set; } = [];

        /// <summary>Ready lists of values, by name: the voltages zener diodes come in, …</summary>
        public Dictionary<string, List<string>> Lists { get; set; } = [];

        /// <summary>In a saved set: what fields get (field name → text), over those of the selected row.</summary>
        public Dictionary<string, string> Patterns { get; set; } = [];

        public string BareUnit => $"{(Bare == "u" ? "µ" : Bare)}{Unit}";

        // Built from the type's own text, escaped; a unit can't make them match anything unexpected
        private Regex? _codePattern;
        private Regex? _textPattern;

        private string UnitPattern => Unit == "Ohm" ? "(?i:ohms?|Ω|r)" : $"(?i:{Regex.Escape(Unit)})";

        internal Regex CodePattern => _codePattern ??= new Regex(
            $@"^(?<a>\d*)(?<p>[pnuµμmkKMGgPNU{Regex.Escape(CodeLetter.ToUpperInvariant() + CodeLetter.ToLowerInvariant())}])(?<b>\d*)$");

        internal Regex TextPattern => _textPattern ??= new Regex(
            $@"^(?<n>\d*\.?\d+)\s*(?<p>[pnuµμmkKMGgPNU]?)\s*(?<u>{UnitPattern})?$");

        public override string ToString() => Name;

        /// <summary>A copy to save as a set, with its own values and fields.</summary>
        public ElementType Copy() => (ElementType)MemberwiseClone();
    }

    /// <summary>
    /// The element types, one JSON file each in %LOCALAPPDATA%\Altium Library Manager\Element types. The standard ones are
    /// written there on first use, so they can be changed like any other; a file is self-contained, so exporting is
    /// writing one out and importing is reading one in. Files are checked when read: they may come from anywhere.
    /// Files that can't be read are skipped and reported, never deleted.
    /// </summary>
    public static class ElementTypes
    {
        private const string FormatId = "altium-library-manager/element-type";
        private const int FormatVersion = 1;
        private const long MaxFileSize = 1024 * 1024;
        private const int MaxNameLength = 64;
        private const int MaxItems = ElementValues.MaxValues;

        private static readonly string[] KnownPrefixes = ["", "p", "n", "u", "m", "k", "M", "G"];

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // µ, Ω and Cyrillic as they are, so the files can be read and edited by hand
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // By name only: a number would let any value through ("style": 99)
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
        };

        // The file layout, kept apart from the class so the format doesn't change with the code
        private sealed record TypeFile(string? Format, int Version, string? Name, string? Unit, ElementStyle Style, string? CodeLetter,
            List<string>? Prefixes, string? Bare, string? Series, string? From, string? To, string? Values, List<string>? ValueFields,
            List<string>? TableWords, List<string>? GeneralWords, Dictionary<string, List<string>>? Lists, Dictionary<string, string>? Patterns);

        public static string Folder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Altium Library Manager", "Element types");

        /// <summary>The types in the folder, sorted by name; the standard ones are written first if it has none.</summary>
        public static List<ElementType> Load(List<string> problems)
        {
            var types = new List<ElementType>();
            try
            {
                Directory.CreateDirectory(Folder);
                if (!Directory.EnumerateFiles(Folder, "*.json").Any())
                    foreach (var type in BuiltInElementTypes.All())
                        Write(type, PathFor(type.Name));

                foreach (var path in Directory.EnumerateFiles(Folder, "*.json"))
                {
                    try
                    {
                        types.Add(Read(path));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
                    {
                        problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Folder}: {ex.Message}");
            }
            // Without the folder (or with nothing readable in it), the window still has something to offer
            if (types.Count == 0)
                types = BuiltInElementTypes.All();
            return types.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>The file of a type, by name.</summary>
        public static string PathFor(string name) => Path.Combine(Folder, TemplateStore.FileNameFor(name) + ".json");

        /// <summary>The type a table's name suggests: the one with the longest word in the name.</summary>
        public static ElementType? ForTable(IReadOnlyCollection<ElementType> types, string table)
        {
            ElementType? Best(Func<ElementType, List<string>> words) => types
                .Select(t => (Type: t, Length: words(t).Where(w => StartsWord(table, w)).Select(w => w.Length).DefaultIfEmpty(0).Max()))
                .Where(t => t.Length > 0)
                .OrderByDescending(t => t.Length)
                .Select(t => t.Type)
                .FirstOrDefault();
            return Best(t => t.TableWords) ?? Best(t => t.GeneralWords);
        }

        // From the start of a word, in any form ("led" in "LEDs", not in "Filtered")
        private static bool StartsWord(string table, string word) =>
            word.Length > 0 && Regex.IsMatch(table, $@"\b{Regex.Escape(word)}", RegexOptions.IgnoreCase);

        /// <summary>Reads and checks a type file; throws <see cref="FormatException"/> with the reason if it isn't one.</summary>
        public static ElementType Read(string path)
        {
            // A type is a few kilobytes; a huge file picked by mistake must not be read into memory
            if (new FileInfo(path).Length > MaxFileSize)
                throw new FormatException("too large for an element type");

            TypeFile? file;
            try
            {
                file = JsonSerializer.Deserialize<TypeFile>(File.ReadAllText(path), JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new FormatException($"not a valid JSON file ({ex.Message})", ex);
            }
            if (file == null || file.Format != FormatId)
                throw new FormatException("not an element type file");
            if (file.Version > FormatVersion)
                throw new FormatException("made by a newer version of the app");

            var type = new ElementType
            {
                Name = file.Name?.Trim() is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(path),
                Unit = file.Unit?.Trim() ?? "",
                Style = file.Style,
                CodeLetter = file.CodeLetter?.Trim() ?? "",
                Prefixes = file.Prefixes ?? [""],
                Bare = file.Bare ?? "",
                Series = file.Series ?? "E24",
                From = file.From ?? "",
                To = file.To ?? "",
                Values = file.Values ?? "",
                ValueFields = file.ValueFields ?? ["Value"],
                TableWords = file.TableWords ?? [],
                GeneralWords = file.GeneralWords ?? [],
                Lists = file.Lists ?? [],
                Patterns = file.Patterns ?? []
            };
            Check(type);
            return type;
        }

        private static void Check(ElementType type)
        {
            if (type.Name.Length > MaxNameLength)
                throw new FormatException($"the name is over {MaxNameLength} characters");
            if (type.Unit.Length > 8)
                throw new FormatException("the unit is too long");
            if (type.CodeLetter.Length > 1 || type.CodeLetter.Any(c => !char.IsLetter(c)))
                throw new FormatException("the code letter has to be one letter");
            if (type.Prefixes.Count == 0 || type.Prefixes.Concat([type.Bare]).Any(p => !KnownPrefixes.Contains(p)))
                throw new FormatException($"prefixes can only be: {string.Join(" ", KnownPrefixes.Skip(1))}, or empty");
            if (!ElementValues.IsKnownSeries(type.Series))
                throw new FormatException($"there is no series \"{type.Series}\"");
            if (type.Lists.Count > 100 || type.Lists.Values.Any(l => l == null || l.Count > MaxItems) || type.Patterns.Count > 500)
                throw new FormatException("too many values");
            if (type.ValueFields.Concat(type.TableWords).Concat(type.GeneralWords).Concat(type.Lists.Values.SelectMany(l => l)).Concat(type.Patterns.Values).Any(s => s == null))
                throw new FormatException("a list has an empty entry");
        }

        /// <summary>Writes the type file (also used for export); replaces the file only once fully written.</summary>
        public static void Write(ElementType type, string path)
        {
            var file = new TypeFile(FormatId, FormatVersion, type.Name, type.Unit, type.Style, type.CodeLetter, type.Prefixes, type.Bare, type.Series,
                type.From, type.To, type.Values, type.ValueFields, type.TableWords, type.GeneralWords, type.Lists, type.Patterns);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>The standard element types, written to the types folder the first time it is used.</summary>
    public static class BuiltInElementTypes
    {
        private static ElementType Number(string name, string unit, ElementStyle style, string letter, string[] prefixes, string bare,
            string series, string from, string to, string[] valueFields, string[] tableWords, string[]? generalWords = null) => new()
            {
                Name = name,
                Unit = unit,
                Style = style,
                CodeLetter = letter,
                Prefixes = [.. prefixes],
                Bare = bare,
                Series = series,
                From = from,
                To = to,
                ValueFields = ["Value", .. valueFields],
                TableWords = [.. tableWords],
                GeneralWords = [.. generalWords ?? []]
            };

        private static ElementType Text(string name, string[] tableWords, string listName, string[] list) => new()
        {
            Name = name,
            Style = ElementStyle.Text,
            TableWords = [.. tableWords],
            Lists = new() { [listName] = [.. list] },
            Values = string.Join(Environment.NewLine, list)
        };

        private static ElementType With(this ElementType type, string name, params string[] list)
        {
            type.Lists[name] = [.. list];
            return type;
        }

        private static ElementType Start(this ElementType type, string name)
        {
            type.Values = string.Join(Environment.NewLine, type.Lists[name]);
            return type;
        }

        public static List<ElementType> All()
        {
            string[] ohm = ["", "k", "M", "G"], farad = ["p", "n", "u", ""], henry = ["n", "u", "m", ""], hertz = ["", "k", "M", "G"];
            string[] amp = ["m", ""], volt = [""], resistance = ["Resistance"], capacitance = ["Capacitance"], inductance = ["Inductance"],
                voltage = ["Voltage", "Voltage - Rated", "Voltage - Zener (Nom) (Vz)"], frequency = ["Frequency"], current = ["Current", "Current Rating"];
            return
            [
                Number("Resistor", "Ohm", ElementStyle.Letter, "R", ohm, "", "E24", "10", "1M", resistance, [], ["resistor"]),
                Number("Resistor, precision", "Ohm", ElementStyle.Letter, "R", ohm, "", "E96+E24", "10", "1M", resistance, ["precision resistor"]),
                Number("Current sense resistor", "Ohm", ElementStyle.Letter, "R", ohm, "", "E6", "0.001", "1", resistance, ["current sense", "shunt"])
                    .With("Common shunts", "0R001", "0R002", "0R005", "0R01", "0R015", "0R02", "0R025", "0R03", "0R05", "0R1", "0R15", "0R2", "0R25", "0R33", "0R5", "1R"),
                Number("Resistor network", "Ohm", ElementStyle.Letter, "R", ohm, "", "E12", "10", "1M", resistance, ["resistor network", "arrays"]),
                Number("Trimmer, potentiometer", "Ohm", ElementStyle.Letter, "R", ohm, "", "1-2-5", "100", "1M", resistance, ["potentiometer", "trimmer potentiometer"]),
                Number("Thermistor NTC", "Ohm", ElementStyle.Letter, "R", ohm, "", "E6", "1K", "100K", resistance, ["thermistor", "ntc"])
                    .With("Common values", "1K", "2K2", "4K7", "10K", "22K", "47K", "50K", "100K"),
                Number("Varistor", "V", ElementStyle.Suffix, "", volt, "", "E12", "10", "100", voltage, ["varistor", "mov"])
                    .With("Varistor voltages (AC)", "11V", "14V", "17V", "20V", "25V", "30V", "35V", "40V", "50V", "60V", "75V", "95V", "115V", "130V", "150V", "175V", "230V", "250V", "275V", "300V", "320V", "385V", "420V", "460V", "510V", "550V"),

                Number("Capacitor, ceramic", "F", ElementStyle.Suffix, "", farad, "p", "E12", "1p", "10u", capacitance, ["ceramic"], ["capacitor"]),
                Number("Capacitor, electrolytic", "F", ElementStyle.Suffix, "", farad, "u", "E6", "1u", "10000u", capacitance, ["electrolytic", "aluminum"]),
                Number("Capacitor, tantalum", "F", ElementStyle.Suffix, "", farad, "u", "E6", "1u", "1000u", capacitance, ["tantalum"]),
                Number("Capacitor, film", "F", ElementStyle.Suffix, "", farad, "n", "E6", "1n", "10u", capacitance, ["film"]),
                Number("Supercapacitor", "F", ElementStyle.Suffix, "", farad, "", "E6", "0.1", "100", capacitance, ["supercap", "edlc"]),

                Number("Inductor", "H", ElementStyle.Suffix, "", henry, "u", "E12", "1u", "1000u", inductance, [], ["inductor", "choke"]),
                Number("RF inductor", "H", ElementStyle.Suffix, "", henry, "n", "E24", "1n", "1000n", inductance, ["rf inductor"]),
                Number("Ferrite bead", "Ohm", ElementStyle.Letter, "R", ohm, "", "E6", "10", "2K2", ["Impedance", "Impedance @ Frequency"], ["ferrite", "bead"])
                    .With("Impedance at 100 MHz", "10R", "30R", "33R", "47R", "60R", "80R", "100R", "120R", "150R", "180R", "220R", "300R", "330R", "470R", "600R", "1K", "1K5", "2K2")
                    .Start("Impedance at 100 MHz"),

                Number("Zener diode", "V", ElementStyle.Letter, "V", volt, "", "E24", "2.4", "75", voltage, ["zener"])
                    .With("BZX series", "2V4", "2V7", "3V", "3V3", "3V6", "3V9", "4V3", "4V7", "5V1", "5V6", "6V2", "6V8", "7V5", "8V2", "9V1", "10V", "11V", "12V", "13V", "15V", "16V", "18V", "20V", "22V", "24V", "27V", "30V", "33V", "36V", "39V", "43V", "47V", "51V", "56V", "62V", "68V", "75V")
                    .Start("BZX series"),
                Number("TVS diode", "V", ElementStyle.Letter, "V", volt, "", "E24", "3.3", "100", voltage, ["tvs"])
                    .With("Standoff voltages", "3V3", "5V", "6V", "6V5", "7V", "7V5", "8V", "8V5", "9V", "10V", "11V", "12V", "13V", "14V", "15V", "16V", "17V", "18V", "20V", "22V", "24V", "26V", "28V", "30V", "33V", "36V", "40V", "43V", "45V", "48V", "51V", "54V", "58V", "60V", "64V", "70V", "75V", "78V", "85V", "90V", "100V")
                    .Start("Standoff voltages"),
                Number("Voltage regulator", "V", ElementStyle.Letter, "V", volt, "", "E24", "1", "24", ["Voltage - Output", "Voltage - Output (Min/Fixed)"], ["regulator", "ldo"])
                    .With("Output voltages", "1V", "1V2", "1V5", "1V8", "2V5", "2V8", "3V", "3V3", "3V6", "4V", "5V", "6V", "8V", "9V", "10V", "12V", "15V", "18V", "24V")
                    .Start("Output voltages"),
                Number("Voltage reference", "V", ElementStyle.Letter, "V", volt, "", "E24", "1", "10", ["Voltage - Output"], ["reference"])
                    .With("Reference voltages", "1V2", "1V25", "2V048", "2V5", "3V", "3V3", "4V096", "4V5", "5V", "10V")
                    .Start("Reference voltages"),

                Number("Crystal", "Hz", ElementStyle.Suffix, "", hertz, "", "E24", "1M", "50M", frequency, ["crystal"])
                    .With("Common frequencies", "32.768kHz", "1MHz", "1.8432MHz", "2MHz", "3.579545MHz", "3.6864MHz", "4MHz", "4.9152MHz", "6MHz", "7.3728MHz", "8MHz", "10MHz", "11.0592MHz", "12MHz", "14.31818MHz", "14.7456MHz", "16MHz", "18.432MHz", "20MHz", "24MHz", "25MHz", "26MHz", "27MHz", "32MHz", "40MHz", "48MHz", "50MHz")
                    .Start("Common frequencies"),
                Number("Oscillator", "Hz", ElementStyle.Suffix, "", hertz, "", "E24", "1M", "100M", frequency, ["oscillator"])
                    .With("Common frequencies", "32.768kHz", "1MHz", "4MHz", "8MHz", "10MHz", "12MHz", "16MHz", "20MHz", "24MHz", "25MHz", "26MHz", "27MHz", "32MHz", "40MHz", "48MHz", "50MHz", "100MHz", "125MHz", "156.25MHz")
                    .Start("Common frequencies"),

                Number("Fuse", "A", ElementStyle.Suffix, "", amp, "", "R10", "50m", "20", current, ["fuse"])
                    .With("Common ratings", "50mA", "63mA", "100mA", "125mA", "160mA", "200mA", "250mA", "315mA", "400mA", "500mA", "630mA", "800mA", "1A", "1.25A", "1.6A", "2A", "2.5A", "3.15A", "4A", "5A", "6.3A", "8A", "10A", "12A", "15A", "20A"),
                Number("Resettable fuse (PTC)", "A", ElementStyle.Suffix, "", amp, "", "R10", "50m", "10", current, ["ptc", "polyfuse", "resettable"])
                    .With("Hold currents", "50mA", "100mA", "125mA", "200mA", "250mA", "300mA", "350mA", "500mA", "750mA", "1.1A", "1.5A", "2A", "2.6A", "3A", "4A", "5A")
                    .Start("Hold currents"),

                Text("LED", ["led"], "Colours", ["Red", "Green", "Blue", "Yellow", "Orange", "Amber", "White", "Warm white", "Pink", "Purple", "IR", "UV"]),
                Text("Pin header", ["header"], "Single row", ["1x02", "1x03", "1x04", "1x05", "1x06", "1x08", "1x10", "1x12", "1x16", "1x20", "1x40"])
                    .With("Double row", "2x02", "2x03", "2x04", "2x05", "2x06", "2x08", "2x10", "2x13", "2x17", "2x20", "2x25", "2x32", "2x40"),
                Text("Terminal block", ["terminal"], "Positions", ["2P", "3P", "4P", "5P", "6P", "8P", "10P", "12P"]),
                new ElementType { Name = "Other", Style = ElementStyle.Text }
            ];
        }
    }
}
