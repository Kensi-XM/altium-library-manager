using System.Text.RegularExpressions;

namespace Altium_Library_Manager.Services
{
    /// <summary>
    /// What the search boxes (table list filter, row search) match: every word typed must be found, in any order and
    /// anywhere, ignoring case ("0603 10k" finds "RES 10K 1% 0603"). Words in quotes are matched as one phrase.
    /// </summary>
    public static partial class SearchText
    {
        [GeneratedRegex("\"(?<w>[^\"]+)\"?|(?<w>[^\\s\"]+)")]
        private static partial Regex WordPattern();

        public static IReadOnlyList<string> Words(string? text) => string.IsNullOrWhiteSpace(text)
            ? []
            : WordPattern().Matches(text).Select(m => m.Groups["w"].Value.Trim()).Where(w => w.Length > 0).ToList();

        public static bool Matches(string text, IReadOnlyList<string> words)
        {
            foreach (var word in words)
                if (!text.Contains(word, StringComparison.CurrentCultureIgnoreCase))
                    return false;
            return true;
        }
    }
}
