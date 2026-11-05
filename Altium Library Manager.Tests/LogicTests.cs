using System.Globalization;
using System.IO;
using Altium_Library_Manager.Services;
using static Altium_Library_Manager.Tests.T;

namespace Altium_Library_Manager.Tests
{
    /// <summary>What needs no window or database: values, series, types, links, search words.</summary>
    public static class LogicTests
    {
        private static readonly List<ElementType> Types = BuiltInElementTypes.All();

        private static ElementType Type(string name) => Types.First(t => t.Name == name);

        public static void Run()
        {
            Section("Series");
            foreach (var (name, count) in new[] { ("E3", 3), ("E6", 6), ("E12", 12), ("E24", 24), ("E48", 48), ("E96", 96), ("E192", 192), ("R10", 10), ("1-2-5", 3) })
                Test($"{name} has {count} values per decade", () => Equal(count, ElementValues.Series(name, 1, 9.9999).Count, name));
            Test("E12 is the published series", () => Equal("1 1.2 1.5 1.8 2.2 2.7 3.3 3.9 4.7 5.6 6.8 8.2",
                string.Join(" ", ElementValues.Series("E12", 1, 9.9).Select(v => v.ToString(CultureInfo.InvariantCulture))), "E12"));
            Test("E96 starts as published", () => Equal("1 1.02 1.05 1.07 1.1 1.13 1.15 1.18 1.21 1.24 1.27 1.3 1.33 1.37 1.4",
                string.Join(" ", ElementValues.Series("E96", 1, 1.4).Select(v => v.ToString(CultureInfo.InvariantCulture))), "E96"));
            Test("E96 ends as published", () => Equal("9.09 9.31 9.53 9.76",
                string.Join(" ", ElementValues.Series("E96", 9, 9.9).Select(v => v.ToString(CultureInfo.InvariantCulture))), "E96"));
            Test("E192 has 9.20, not 9.19", () => True(ElementValues.Series("E192", 9, 9.5).Contains(9.2) && !ElementValues.Series("E192", 9, 9.5).Contains(9.19), "9.20"));
            Test("the range includes both ends", () => Equal("100 120 150 180 220 270 330 390 470 560 680 820 1000",
                string.Join(" ", ElementValues.Series("E12", 100, 1000)), "range"));
            Test("values stay exact in small decades", () => True(ElementValues.Series("E24", 1e-12, 1e-6).All(v => v.ToString("R", CultureInfo.InvariantCulture).Length < 12), "digits"));
            Test("no values for a wrong range", () =>
            {
                Equal(0, ElementValues.Series("E12", 0, 10).Count, "from 0");
                Equal(0, ElementValues.Series("E12", -5, 10).Count, "negative");
                Equal(0, ElementValues.Series("E12", 10, 1).Count, "reversed");
                Equal(0, ElementValues.Series("nope", 1, 10).Count, "unknown");
            });
            Test("an endless range stops", () =>
            {
                Equal(0, ElementValues.Series("E12", double.PositiveInfinity, double.PositiveInfinity).Count, "infinite from");
                True(ElementValues.Series("E12", 1, double.PositiveInfinity).Count <= ElementValues.MaxValues + 200, "infinite to");
                Equal(0, ElementValues.Series("E12", double.NaN, 10).Count, "NaN");
            });
            Test("a very wide range is cut off, not hanging", () => True(ElementValues.Series("E192", 1e-300, 1e300).Count <= ElementValues.MaxValues + 200, "wide"));

            Section("Writing and reading values");
            foreach (var type in Types.Where(t => t.Style != ElementStyle.Text))
                Test($"{type.Name}: every value reads back", () =>
                {
                    foreach (var value in ElementValues.Series("E96", 1e-11, 9e8))
                    {
                        var written = ElementValues.Format(type, value);
                        foreach (var text in new[] { written.Code, written.Text, written.Short, written.Code.ToUpperInvariant() })
                        {
                            // A capital form can mean something else only for M/m, which the type's prefixes decide
                            if (text == written.Code.ToUpperInvariant() && type.Prefixes.Contains("m") && type.Prefixes.Contains("M"))
                                continue;
                            Near(value, ElementValues.Parse(type, text), $"{type.Name} [{text}]");
                        }
                    }
                });
            foreach (var (name, text, expected) in new[]
                     {
                         ("Resistor", "4K7", 4700), ("Resistor", "4k7", 4700), ("Resistor", "0R56", 0.56), ("Resistor", "R47", 0.47), ("Resistor", "1M5", 1.5e6), ("Resistor", "5m", 0.005),
                         ("Resistor", "4.7k", 4700), ("Resistor", "4,7 kOhm", 4700), ("Resistor", "10 Ohm", 10), ("Resistor", "10 ohms", 10), ("Resistor", "10Ω", 10), ("Resistor", "100", 100),
                         ("Capacitor, ceramic", "100", 1e-10), ("Capacitor, ceramic", "100n", 1e-7), ("Capacitor, ceramic", "100NF", 1e-7), ("Capacitor, ceramic", "4u7", 4.7e-6), ("Capacitor, ceramic", "1µF", 1e-6), ("Capacitor, ceramic", "1 F", 1),
                         ("Capacitor, electrolytic", "470", 4.7e-4), ("Inductor", "10", 1e-5), ("Inductor", "1mH", 1e-3), ("Inductor", "1MH", 1e-3), ("RF inductor", "22", 2.2e-8),
                         ("Zener diode", "3V3", 3.3), ("Zener diode", "12", 12), ("Crystal", "16M", 1.6e7), ("Crystal", "16MHz", 1.6e7), ("Crystal", "32.768kHz", 32768), ("Fuse", "500m", 0.5), ("Fuse", "500mA", 0.5), ("Fuse", "2", 2)
                     })
                Test($"{name} reads [{text}]", () => Near(expected, ElementValues.Parse(Type(name), text), text));
            foreach (var (name, text) in new[] { ("Resistor", ""), ("Resistor", "abc"), ("Resistor", "R"), ("Resistor", "K"), ("Resistor", "1.2.3"), ("Resistor", "-5"), ("Capacitor, ceramic", "10H"), ("Inductor", "10F"), ("LED", "5"), ("Resistor", "1e5") })
                Test($"{name} doesn't read [{text}]", () => Equal(null, ElementValues.Parse(Type(name), text), text));
            foreach (var (name, value, code, text) in new[]
                     {
                         ("Resistor", 4700d, "4K7", "4.7 kOhm"), ("Resistor", 0.56, "0R56", "0.56 Ohm"), ("Resistor", 100, "100R", "100 Ohm"), ("Resistor", 1000, "1K", "1 kOhm"), ("Resistor", 62e6, "62M", "62 MOhm"), ("Resistor", 445.36, "445R36", "445.36 Ohm"), ("Resistor", 0.005, "0R005", "0.005 Ohm"),
                         ("Capacitor, ceramic", 2.2e-11, "22pF", "22 pF"), ("Capacitor, ceramic", 1e-7, "100nF", "100 nF"), ("Capacitor, ceramic", 4.7e-6, "4.7uF", "4.7 µF"), ("Capacitor, electrolytic", 1e-3, "1000uF", "1000 µF"),
                         ("Inductor", 1e-5, "10uH", "10 µH"), ("Inductor", 1e-3, "1mH", "1 mH"), ("Zener diode", 3.3, "3V3", "3.3 V"), ("Zener diode", 5, "5V", "5 V"), ("Crystal", 3579545, "3.579545MHz", "3.579545 MHz"), ("Fuse", 0.5, "500mA", "500 mA"), ("Fuse", 3.15, "3.15A", "3.15 A")
                     })
                Test($"{name} writes {value}", () =>
                {
                    var written = ElementValues.Format(Type(name), value);
                    Equal(code, written.Code, "code");
                    Equal(text, written.Text, "text");
                });
            Test("the language of Windows doesn't change values", () =>
            {
                var culture = CultureInfo.CurrentCulture;
                try
                {
                    foreach (var name in new[] { "ru-RU", "de-DE", "tr-TR" })
                    {
                        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                        Equal("4K7", ElementValues.Format(Type("Resistor"), 4700).Code, name);
                        Near(4700, ElementValues.Parse(Type("Resistor"), "4.7K"), name);
                        Equal("4.7uF", ElementValues.Format(Type("Capacitor, ceramic"), 4.7e-6).Code, name);
                        Near(1e-7, ElementValues.Parse(Type("Capacitor, ceramic"), "100NF"), name);
                        Equal("1.5", ElementValues.Series("E12", 1.5, 1.5)[0].ToString(CultureInfo.InvariantCulture), name);
                    }
                }
                finally
                {
                    CultureInfo.CurrentCulture = culture;
                }
            });
            Test("lists split as expected", () =>
            {
                var type = Type("Resistor");
                Equal("100R 120R 4K7 1K 2K 3K 4K7", string.Join(" ", ElementValues.ParseList(type, "100, 120\n4.7 kOhm; 1K 2K 3K\r\n4K7").Select(v => v.Code)), "list");
                Equal(0, ElementValues.ParseList(type, " ,;\n").Count, "empty");
                Equal("Warm white|Red", string.Join("|", ElementValues.ParseList(Type("LED"), "Warm white, Red").Select(v => v.Code)), "text list");
                Equal("something else", ElementValues.ParseList(type, "something else")[0].Code, "not a value stays");
            });

            Section("Patterns");
            var r = ElementValues.FromText(Type("Resistor"), "0R56");
            foreach (var (text, pattern) in new[]
                     {
                         ("RES 0R56 0.1%", "RES {Value} 0.1%"), ("RES 0.56 Ohm 1/20W", "RES {Text} 1/20W"), ("0R56", "{Value}"), ("RES 10R56 X", "RES 10R56 X"), ("X0R56", "X0R56"), ("=Value", "=Value"), ("", ""), ("0R56 0R56", "{Value} {Value}")
                     })
                Test($"pattern of [{text}]", () => Equal(pattern, ElementValues.ToPattern(text, r), text));
            var c = ElementValues.FromText(Type("Capacitor, ceramic"), "33 µF");
            foreach (var (text, pattern) in new[] { ("CAP 33UF 20%", "CAP {VALUE} 20%"), ("33 µF", "{Text}"), ("33µF", "{Short}"), ("33uF", "{Value}"), ("133UF", "133UF") })
                Test($"pattern of [{text}]", () => Equal(pattern, ElementValues.ToPattern(text, c), text));
            Test("a pattern with regex characters in the value", () =>
            {
                var odd = new ElementValue("a+b(c", "a+b(c");
                Equal("X {Value} Y", ElementValues.ToPattern("X a+b(c Y", odd), "odd");
                var dollar = new ElementValue("$1", "$1");
                Equal("X {Value}", ElementValues.ToPattern("X $1", dollar), "dollar");
            });
            Test("a pattern and its value give the text back", () =>
            {
                foreach (var text in new[] { "RES 0R56 0.1%", "RES 0.56 Ohm 1/20W", "plain" })
                    Equal(text, ElementValues.Apply(ElementValues.ToPattern(text, r), r, 1), text);
            });
            Test("all placeholders", () => Equal("4K7 4K7 4.7 kOhm 4.7kOhm 3 {other}",
                ElementValues.Apply("{Value} {VALUE} {Text} {Short} {N} {other}", ElementValues.Format(Type("Resistor"), 4700), 3), "apply"));

            Section("Element types");
            foreach (var (table, type) in new[]
                     {
                         ("Resistors 0402", "Resistor"), ("Resistors Surface Mount 0805", "Resistor"), ("Resistor Networks Arrays", "Resistor network"), ("Capacitors Ceramic 0603", "Capacitor, ceramic"),
                         ("Capacitors Aluminum Electrolytic TH", "Capacitor, electrolytic"), ("Capacitors Aluminum Polymer SMT", "Capacitor, electrolytic"), ("Capacitors Tantalum", "Capacitor, tantalum"), ("Capacitors Film", "Capacitor, film"), ("Inductors", "Inductor"), ("Diodes Zener", "Zener diode"),
                         ("Crystals", "Crystal"), ("Fuses", "Fuse"), ("LEDs", "LED")
                     })
                Test($"table [{table}] is {type}", () => Equal(type, ElementTypes.ForTable(Types, table)?.Name, table));
            foreach (var table in new[] { "Filtered Connectors", "Amplifiers Audio", "Battery Management", "", "Removal" })
                Test($"table [{table}] has no type", () => Equal(null, ElementTypes.ForTable(Types, table)?.Name, table));
            Test("no types at all", () => Equal(null, ElementTypes.ForTable([], "Resistors"), "empty"));

            var folder = Path.Combine(Path.GetTempPath(), "alm-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "type.json");
            foreach (var type in Types)
                Test($"{type.Name} goes through a file unchanged", () =>
                {
                    ElementTypes.Write(type, file);
                    var back = ElementTypes.Read(file);
                    Equal(type.Name, back.Name, "name");
                    Equal(type.Unit, back.Unit, "unit");
                    Equal(type.Style, back.Style, "style");
                    Equal(string.Join(",", type.Prefixes), string.Join(",", back.Prefixes), "prefixes");
                    Equal(type.Values, back.Values, "values");
                    Equal(type.Lists.Count, back.Lists.Count, "lists");
                    Equal(string.Join(",", type.TableWords), string.Join(",", back.TableWords), "words");
                    Equal(string.Join(",", type.GeneralWords), string.Join(",", back.GeneralWords), "general words");
                });
            foreach (var type in Types.Where(t => t.Style != ElementStyle.Text))
                Test($"{type.Name}: what it offers can be read", () =>
                {
                    True(ElementValues.Parse(type, type.From) != null, $"from [{type.From}]");
                    True(ElementValues.Parse(type, type.To) != null, $"to [{type.To}]");
                    foreach (var value in type.Lists.Values.SelectMany(l => l))
                        True(ElementValues.Parse(type, value) != null, $"list value [{value}]");
                });
            Test("names keep the file in its folder", () =>
            {
                foreach (var name in new[] { @"..\..\evil", "../../evil", @"C:\evil", "CON", "NUL", "a:b*c?", "  " })
                {
                    var path = ElementTypes.PathFor(name);
                    Equal(ElementTypes.Folder, Path.GetDirectoryName(Path.GetFullPath(path)), name);
                    True(!Path.GetFileNameWithoutExtension(path).Equals("CON", StringComparison.OrdinalIgnoreCase), "device name");
                }
            });

            const string head = "\"format\":\"altium-library-manager/element-type\",\"version\":1,\"name\":\"X\"";
            void Refused(string what, string json) => Test($"file refused: {what}", () =>
            {
                File.WriteAllText(file, json);
                Throws<FormatException>(() => ElementTypes.Read(file), what);
            });
            void Safe(string what, string json, Action<ElementType> check) => Test($"file safe: {what}", () =>
            {
                File.WriteAllText(file, json);
                ElementType type;
                try
                {
                    type = ElementTypes.Read(file);
                }
                catch (FormatException)
                {
                    return;   // refused is safe too
                }
                check(type);
            });
            Refused("not JSON", "{{{");
            Refused("empty", "");
            Refused("the word null", "null");
            Refused("an array", "[]");
            Refused("another format", "{\"format\":\"x\"}");
            Refused("a newer version", "{\"format\":\"altium-library-manager/element-type\",\"version\":9}");
            Refused("style as a number", "{" + head + ",\"style\":99}");
            Refused("unknown style", "{" + head + ",\"style\":\"Nope\"}");
            Refused("unknown prefix", "{" + head + ",\"prefixes\":[\"x\"]}");
            Refused("no prefixes", "{" + head + ",\"prefixes\":[]}");
            Refused("unknown bare prefix", "{" + head + ",\"bare\":\"x\"}");
            Refused("unknown series", "{" + head + ",\"series\":\"E5\"}");
            Refused("code letter not a letter", "{" + head + ",\"codeLetter\":\"]\"}");
            Refused("a long unit", "{" + head + ",\"unit\":\"aaaaaaaaaaaaaaaaaaaaaaaa\"}");
            Refused("a long name", "{\"format\":\"altium-library-manager/element-type\",\"version\":1,\"name\":\"" + new string('x', 100) + "\"}");
            var nulls = new Action<ElementType>(t =>
            {
                ElementTypes.ForTable([t], "Resistors");
                True(t.ValueFields.All(f => f != null), "value fields");
                True(t.Lists.Values.All(l => l.All(v => v != null)), "lists");
                True(t.Patterns.Values.All(v => v != null), "patterns");
                ElementValues.ParseList(t, t.Values);
            });
            Safe("null in table words", "{" + head + ",\"tableWords\":[null],\"generalWords\":[null]}", nulls);
            Safe("null in value fields", "{" + head + ",\"valueFields\":[null]}", nulls);
            Safe("null in a list", "{" + head + ",\"lists\":{\"a\":[null,\"1\"]}}", nulls);
            Safe("a null list", "{" + head + ",\"lists\":{\"a\":null}}", nulls);
            Safe("null pattern", "{" + head + ",\"patterns\":{\"Value\":null}}", nulls);
            Safe("regex in the unit", "{" + head + ",\"unit\":\"(a+)+$\"}", t =>
            {
                Equal(null, ElementValues.Parse(t, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!"), "unit as text");
                Near(1, ElementValues.Parse(t, "1"), "bare");
            });
            Test("a huge file is refused unread", () =>
            {
                File.WriteAllText(file, new string(' ', 2_000_000));
                Throws<FormatException>(() => ElementTypes.Read(file), "huge");
            });
            Test("a missing name takes the file's", () =>
            {
                File.WriteAllText(file, "{\"format\":\"altium-library-manager/element-type\",\"version\":1}");
                Equal("type", ElementTypes.Read(file).Name, "name");
            });
            Test("µ and Ω stay readable in the file", () =>
            {
                ElementTypes.Write(Type("Capacitor, ceramic"), file);
                var type = new ElementType { Name = "Ω µ Кириллица", Unit = "Ω" };
                ElementTypes.Write(type, file);
                True(File.ReadAllText(file).Contains("Ω µ Кириллица"), "text");
                Equal("Ω µ Кириллица", ElementTypes.Read(file).Name, "back");
            });
            Directory.Delete(folder, true);

            Section("Links");
            foreach (var (field, link) in new[]
                     {
                         ("Library Path", FieldLink.Symbol), ("Footprint Path", FieldLink.Footprint), ("Footprint Path 2", FieldLink.Footprint), ("ComponentLink1URL", FieldLink.Document), ("componentlink2url", FieldLink.Document),
                         ("Library Ref", FieldLink.SymbolRef), ("LibRef", FieldLink.SymbolRef), ("Footprint Ref", FieldLink.FootprintRef), ("Footprint Ref 3", FieldLink.FootprintRef), ("Model Path", FieldLink.File),
                         ("ComponentLink1Description", FieldLink.None), ("Part Number", FieldLink.None), ("Description", FieldLink.None), ("Reference", FieldLink.None), ("Calibration Ref", FieldLink.None)
                     })
                Test($"field [{field}]", () => Equal(link, FieldLinks.Classify(field), field));
            foreach (var (value, address) in new (string, string?)[]
                     {
                         ("http://a.com/x y", "http://a.com/x%20y"), ("https://a.com", "https://a.com/"), ("www.a.com/x", "https://www.a.com/x"), ("  https://a.com  ", "https://a.com/"), ("HTTP://A.COM", "http://a.com/"),
                         ("javascript:alert(1)", null), ("file:///C:/Windows/System32/calc.exe", null), ("ms-msdt:/id", null), ("calc.exe", null), ("C:\\x.pdf", null), ("\\\\server\\x", null), ("mailto:a@b.c", null), ("ftp://a.com", null), ("data:text/html,x", null), ("", null), ("search-ms:query=x", null)
                     })
                Test($"web address [{value}]", () => Equal(address, FieldLinks.WebAddress(value), value));
            foreach (var (file2, document) in new[] { ("a.pdf", true), ("A.DOCX", true), ("a.exe", false), ("a.bat", false), ("a.cmd", false), ("a.lnk", false), ("a.js", false), ("a.ps1", false), ("a.hta", false), ("a.url", false), ("a.pdf.exe", false), ("a", false), ("a.SchLib", false) })
                Test($"document [{file2}]", () => Equal(document, FieldLinks.IsDocument(file2), file2));
            foreach (var (path, example, stored) in new (string, string?, string)[]
                     {
                         (@"D:\Lib\sym\a.SchLib", "x/y.SchLib", "sym/a.SchLib"), (@"D:\Lib\sym\a.SchLib", @"x\y.SchLib", @"sym\a.SchLib"), (@"D:\Lib\sym\a.SchLib", null, @"sym\a.SchLib"), (@"D:\Lib\a.SchLib", "x/y", "a.SchLib"),
                         (@"E:\Other\a.SchLib", null, @"E:\Other\a.SchLib"), (@"D:\Lib2\a.SchLib", null, @"D:\Lib2\a.SchLib"), (@"D:\a.SchLib", null, @"D:\a.SchLib"), (@"D:\Lib\..foo\a.SchLib", null, @"..foo\a.SchLib"), (@"\\srv\s\a.SchLib", null, @"\\srv\s\a.SchLib")
                     })
                Test($"stored path of [{path}]", () => Equal(stored, FieldLinks.StoredPath(path, @"D:\Lib", example), path));
            Test("a footprint's library", () =>
            {
                Equal("Footprint Path 2|Footprint Path|Footprint Path 1", string.Join("|", FieldLinks.LibraryFields("Footprint Ref 2", FieldLink.FootprintRef)), "2");
                Equal("Footprint Path|Footprint Path 1", string.Join("|", FieldLinks.LibraryFields("Footprint Ref", FieldLink.FootprintRef)), "main");
                Equal("Library Path", FieldLinks.LibraryFields("Library Ref", FieldLink.SymbolRef)[0], "symbol");
            });
            Test("what can be opened", () =>
            {
                True(FieldLinks.CanOpen("x", FieldLink.Symbol), "path");
                True(!FieldLinks.CanOpen("x", FieldLink.SymbolRef), "ref");
                True(!FieldLinks.CanOpen("  ", FieldLink.Document), "empty");
                True(FieldLinks.CanOpen("https://a.com", FieldLink.None), "address anywhere");
                True(!FieldLinks.CanOpen("plain", FieldLink.None), "plain text");
            });

            Section("Search words");
            foreach (var (text, words) in new[]
                     {
                         ("10k 0603", "10k|0603"), ("  a   b ", "a|b"), ("\"two words\" x", "two words|x"), ("\"open quote", "open quote"), ("\"", ""), ("", ""), ("   ", ""), ("a\"b", "a|b"), ("\"\"", "")
                     })
                Test($"words of [{text}]", () => Equal(words, string.Join("|", SearchText.Words(text)), text));
            Test("all words have to match, any order, any case", () =>
            {
                True(SearchText.Matches("RES 10K 1% 0603", SearchText.Words("0603 10k")), "both");
                True(!SearchText.Matches("RES 10K 1%", SearchText.Words("0603 10k")), "one missing");
                True(SearchText.Matches("anything", []), "no words");
            });
        }
    }
}
