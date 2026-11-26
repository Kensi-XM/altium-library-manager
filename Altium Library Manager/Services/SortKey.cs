using System.Text;
using System.Text.RegularExpressions;

namespace Altium_Library_Manager.Services
{
    /// <summary>
    /// What a text is sorted by in place of itself: every number in it is written so that text order is number order,
    /// counting a unit prefix or a code letter after it. So 2 comes before 10, and 1 Ohm, 4K7, 4.75 kOhm, 1 MOhm,
    /// 100nF, 1R_0603 and 1K02_0603 sort by their value, not by their letters.
    /// </summary>
    public static partial class SortKey
    {
        // A code (4K7, 1R02, 4n7: the letter is the decimal point) or a number with an optional prefix (1 kOhm, 100nF,
        // 1K_0603). A prefix counts only before a unit or the end of the word, so "2 Max" or "M3" keep their letters.
        [GeneratedRegex(
            @"(?<int>\d+)(?<prefix>[pnuµμmkKMGRпнмкМГ]|мк)(?<code>\d+)(?![.,]\d)" +
            @"|(?<int>\d+)(?:[.,](?<frac>\d+))?(?:\s?(?<prefix>[pnuµμmkKMGпнмкМГ]|мк)(?=(?:Ohms?|ohms?|OHMS?|Ω|R|F|H|Hz|V|A|W|s|Ом|Ф|Гн|Гц|В|А|Вт)?(?![\p{L}\d])))?")]
        private static partial Regex Number();

        /// <summary>The sort key of a text; a text without digits is its own key.</summary>
        public static string For(string text)
        {
            if (!text.AsSpan().ContainsAnyInRange('0', '9'))
                return text;
            return Number().Replace(text, m =>
            {
                var prefix = m.Groups["prefix"].Success ? m.Groups["prefix"].Value : "";
                var frac = m.Groups["code"].Success ? m.Groups["code"].Value : m.Groups["frac"].Value;
                return Encode(m.Groups["int"].Value, frac, Exponent(prefix));
            });
        }

        private static int Exponent(string prefix) => prefix switch
        {
            "p" or "п" => -12,
            "n" or "н" => -9,
            "u" or "µ" or "μ" or "мк" => -6,
            "m" or "м" => -3,
            "k" or "K" or "к" => 3,
            "M" or "М" => 6,
            "G" or "Г" => 9,
            _ => 0
        };

        // Fixed width, so equal text before it lines up: 3 digits of the power of ten, then 15 of the digits themselves
        private const int Digits = 15;

        private static string Encode(string whole, string frac, int exponent)
        {
            var all = (whole + frac).TrimStart('0');
            if (all.Length == 0)
                return new string('0', 3 + Digits);
            // The power of ten of the first digit that isn't 0
            var power = whole.TrimStart('0').Length - 1;
            if (power < 0)
                power = -(frac.Length - frac.TrimStart('0').Length) - 1;
            var shifted = Math.Clamp(power + exponent + 500, 1, 999);
            var key = new StringBuilder(3 + Digits);
            key.Append(shifted.ToString("000"));
            key.Append(all.Length >= Digits ? all.AsSpan(0, Digits) : all.PadRight(Digits, '0'));
            return key.ToString();
        }
    }
}
