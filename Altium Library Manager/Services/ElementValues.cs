using System.Globalization;
using System.Text.RegularExpressions;

namespace Altium_Library_Manager.Services
{
    /// <summary>How a type writes its values in part numbers.</summary>
    public enum ElementStyle
    {
        /// <summary>A letter takes the decimal point's place: 4K7, 0R56, 3V3.</summary>
        Letter,
        /// <summary>The unit follows the number: 22pF, 4.7uH, 16MHz.</summary>
        Suffix,
        /// <summary>Plain text, no numbers or units: Red, 1x08.</summary>
        Text
    }

    /// <summary>
    /// One value of a set of elements, as it is written in the fields: <see cref="Code"/> as in part numbers (4K7, 0R56,
    /// 22pF), <see cref="Text"/> as in descriptions (4.7 kOhm, 22 pF).
    /// </summary>
    public sealed record ElementValue(string Code, string Text)
    {
        public string Short => Text.Replace(" ", "");
    }

    /// <summary>A way to generate values, for the series list.</summary>
    public sealed record SeriesInfo(string Name, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>
    /// The values of a set of elements: standard series, lists typed by hand, the ways of writing them, and the patterns
    /// of the fields: {Value} 4K7, {VALUE} in capitals, {Text} 4.7 kOhm, {Short} 4.7kOhm, {N} the number in the set.
    /// How a value reads and is written comes from its <see cref="ElementType"/>, so no kind of element is built in.
    /// </summary>
    public static class ElementValues
    {
        /// <summary>More rows than anyone adds at once, and few enough to stay quick.</summary>
        public const int MaxValues = 5000;

        /// <summary>The E series (IEC 60063) with the tolerance each is made for, and two other common steps.</summary>
        public static readonly IReadOnlyList<SeriesInfo> AllSeries =
        [
            new("E3", "E3 (over 20%)"),
            new("E6", "E6 (20%)"),
            new("E12", "E12 (10%)"),
            new("E24", "E24 (5%)"),
            new("E48", "E48 (2%)"),
            new("E96", "E96 (1%)"),
            new("E192", "E192 (0.5% and better)"),
            new("R10", "R10 (fuses: 1, 1.25, 1.6, 2, …)"),
            new("1-2-5", "1-2-5 (trimmers: 1, 2, 5, 10, …)")
        ];

        private static readonly double[] E24 =
        [
            1.0, 1.1, 1.2, 1.3, 1.5, 1.6, 1.8, 2.0, 2.2, 2.4, 2.7, 3.0,
            3.3, 3.6, 3.9, 4.3, 4.7, 5.1, 5.6, 6.2, 6.8, 7.5, 8.2, 9.1
        ];

        // E3 to E24 are fixed lists; E48 and up follow a formula (with one exception in E192)
        private static IEnumerable<double> Mantissas(string series) => series switch
        {
            "E3" => E24.Where((_, i) => i % 8 == 0),
            "E6" => E24.Where((_, i) => i % 4 == 0),
            "E12" => E24.Where((_, i) => i % 2 == 0),
            "E24" => E24,
            "R10" => [1, 1.25, 1.6, 2, 2.5, 3.15, 4, 5, 6.3, 8],
            "1-2-5" => [1, 2, 5],
            "E48" or "E96" or "E192" when int.TryParse(series.AsSpan(1), out var n) =>
                Enumerable.Range(0, n).Select(i => Math.Round(Math.Pow(10, (double)i / n), 2)).Select(m => m == 9.19 ? 9.2 : m),
            _ => []
        };

        /// <summary>The series' values from <paramref name="from"/> to <paramref name="to"/>, both included.</summary>
        public static List<double> Series(string series, double from, double to)
        {
            var values = new List<double>();
            // Typed digits can overflow to infinity, and the decades below would then never end
            if (!double.IsFinite(from) || !double.IsFinite(to) || from <= 0 || to < from)
                return values;
            var mantissas = Mantissas(series).ToList();
            for (var decade = (int)Math.Floor(Math.Log10(from)); decade <= (int)Math.Floor(Math.Log10(to)) && values.Count <= MaxValues; decade++)
            {
                foreach (var mantissa in mantissas)
                {
                    // Through text, so 4.7 * 10^-6 comes out as written and not 4.7000000000000002E-06
                    var value = double.Parse((mantissa * Math.Pow(10, decade)).ToString("G6", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                    if (double.IsFinite(value) && value >= from * (1 - 1e-9) && value <= to * (1 + 1e-9))
                        values.Add(value);
                }
            }
            return values;
        }

        #region Reading values

        private static double Scale(string prefix) => prefix switch
        {
            "p" => 1e-12,
            "n" => 1e-9,
            "u" or "µ" or "μ" => 1e-6,
            "m" => 1e-3,
            "k" => 1e3,
            "M" => 1e6,
            "G" => 1e9,
            _ => 1
        };

        // Capitals are often just capitals (33UF, 4K7): only M and m differ in meaning. A capital M is mega where the
        // type has mega, and milli otherwise (10MH); a small m is always milli (a 5m shunt).
        private static string Prefix(ElementType type, string letter) => letter switch
        {
            "P" or "N" or "U" => letter.ToLowerInvariant(),
            "K" => "k",
            "g" => "G",
            "M" when !type.Prefixes.Contains("M") && type.Prefixes.Contains("m") => "m",
            _ => letter
        };

        private static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);

        /// <summary>The value in the type's base unit of a text like 4K7, 0R56, 4.7 kOhm, 100, 22pF, 4u7, 33UF, 16 MHz;
        /// null if it isn't one. A bare number is in the type's <see cref="ElementType.BareUnit"/>.</summary>
        public static double? Parse(ElementType type, string text) =>
            ReadNumber(type, text) is { } number && double.IsFinite(number) ? number : null;

        private static double? ReadNumber(ElementType type, string text)
        {
            var value = text.Trim().Replace(',', '.');
            if (value.Length == 0 || type.Style == ElementStyle.Text)
                return null;

            if (type.CodePattern.Match(value) is { Success: true } code)
            {
                var (a, b) = (code.Groups["a"].Value, code.Groups["b"].Value);
                if (a.Length + b.Length == 0)
                    return null;
                var letter = code.Groups["p"].Value;
                var scale = string.Equals(letter, type.CodeLetter, StringComparison.OrdinalIgnoreCase) ? 1 : Scale(Prefix(type, letter));
                return Number($"{(a.Length == 0 ? "0" : a)}.{(b.Length == 0 ? "0" : b)}") * scale;
            }
            if (type.TextPattern.Match(value) is not { Success: true } plain)
                return null;
            var prefix = plain.Groups["p"].Value;
            var bare = prefix.Length == 0 && plain.Groups["u"].Length == 0;
            return Number(plain.Groups["n"].Value) * (bare ? Scale(type.Bare) : Scale(Prefix(type, prefix)));
        }

        /// <summary>
        /// The values of a typed list: separated by lines, commas or semicolons; by spaces too when every part is a
        /// value by itself (so "100 120 150" is three values and "4.7 kOhm" one). What can't be read as a value of the
        /// type is kept as it was typed.
        /// </summary>
        public static List<ElementValue> ParseList(ElementType type, string text)
        {
            var values = new List<ElementValue>();
            foreach (var item in text.Split(['\n', '\r', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = item.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var separate = parts.Length > 1 && type.Style != ElementStyle.Text && parts.All(p => Parse(type, p) != null);
                foreach (var value in separate ? parts : [item])
                    values.Add(FromText(type, value));
            }
            return values;
        }

        public static ElementValue FromText(ElementType type, string text) =>
            Parse(type, text) is { } number ? Format(type, number) : new ElementValue(text.Trim(), text.Trim());

        #endregion

        #region Writing values

        // Nine digits: enough for a crystal (3.579545 MHz), and few enough to drop the noise of doubles (4.7000000000000002)
        private static string Plain(double value)
        {
            var text = value.ToString("G9", CultureInfo.InvariantCulture);
            // Not 1E-11: with an exponent the code couldn't be read back
            return text.Contains('E') && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact)
                ? exact.ToString(CultureInfo.InvariantCulture)
                : text;
        }

        public static ElementValue Format(ElementType type, double value)
        {
            // The largest prefix that leaves a number of 1 or more; the smallest one for less
            var prefixes = type.Prefixes.OrderBy(Scale).ToList();
            var prefix = prefixes.LastOrDefault(p => value / Scale(p) >= 1 - 1e-9) ?? prefixes.FirstOrDefault() ?? "";
            var number = Plain(value / Scale(prefix));
            var written = prefix == "u" ? "µ" : prefix;
            var text = $"{number} {written}{type.Unit}";
            if (type.Style == ElementStyle.Letter)
            {
                var letter = prefix.Length == 0 ? type.CodeLetter : prefix == "k" ? "K" : prefix;
                // The letter stands for the decimal point (4K7, 0R56), or ends the value (10K)
                return new ElementValue(number.Contains('.') ? number.Replace(".", letter) : number + letter, text);
            }
            return new ElementValue($"{number}{prefix}{type.Unit}", text);
        }

        /// <summary>A field's text for one element of the set.</summary>
        public static string Apply(string pattern, ElementValue value, int number) => pattern
            .Replace("{Value}", value.Code, StringComparison.Ordinal)
            .Replace("{VALUE}", value.Code.ToUpperInvariant(), StringComparison.Ordinal)
            .Replace("{Text}", value.Text, StringComparison.Ordinal)
            .Replace("{Short}", value.Short, StringComparison.Ordinal)
            .Replace("{N}", number.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        /// <summary>
        /// Turns a field of an existing element into a pattern: its value, however written, becomes a placeholder
        /// ("RES 0R56 0.1%" → "RES {Value} 0.1%", "RES 0.56 Ohm 1/20W" → "RES {Text} 1/20W"). The longest forms go first.
        /// </summary>
        public static string ToPattern(string text, ElementValue value)
        {
            // The code first: of forms equally long (22pF both ways), {Value} is the one to keep
            var forms = new[] { (value.Code, "{Value}"), (value.Code.ToUpperInvariant(), "{VALUE}"), (value.Short, "{Short}"), (value.Text, "{Text}") }
                .Where(f => f.Item1.Length > 0)
                .OrderByDescending(f => f.Item1.Length);
            foreach (var (form, placeholder) in forms)
            {
                // Whole words only: 22pF in "122pF" isn't this value, and "1R" not inside "RES 1R0"
                text = Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}.{{]){Regex.Escape(form)}(?![\p{{L}}\p{{N}}}}])", placeholder.Replace("$", "$$"));
            }
            return text;
        }

        #endregion
    }
}
