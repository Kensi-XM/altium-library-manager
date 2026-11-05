using System.IO;
using System.Text.RegularExpressions;

namespace Altium_Library_Manager.Services
{
    public enum HelpBlockKind
    {
        Paragraph,
        Heading,
        Bullets,
        Steps,
        Tip,
        Table
    }

    /// <summary>Part of a help topic. <see cref="Lines"/>: the paragraph, or a list's items; <see cref="Rows"/>: a table's
    /// rows, the first one its header unless it describes a term (see <see cref="HelpContent"/>).</summary>
    public sealed record HelpBlock(HelpBlockKind Kind, IReadOnlyList<string> Lines, IReadOnlyList<string[]> Rows);

    /// <summary>A page of the help.</summary>
    public sealed record HelpTopic(string Id, string Title, string Group, IReadOnlyList<HelpBlock> Blocks)
    {
        /// <summary>What a search looks in: the title and the text without its marks.</summary>
        public string SearchText { get; } = Title + "\n" + string.Join("\n", Blocks.SelectMany(b => b.Lines.Concat(b.Rows.SelectMany(r => r))))
            .Replace("**", "").Replace("`", "");

        // Screen readers read list items by their text
        public override string ToString() => Title;
    }

    /// <summary>Part of a line of help text.</summary>
    public enum HelpSpanKind
    {
        Text,
        Bold,
        Code,
        Link
    }

    /// <summary>A piece of a line, with the topic it leads to when it is a link.</summary>
    public sealed record HelpSpan(HelpSpanKind Kind, string Text, string? Target = null);

    /// <summary>
    /// The text of the help: Help/Help.md in the program, in a small markup. "= Group" starts a group of topics, and
    /// "# id | Title" a topic. Then "## Heading", "- item", "1. step", "> tip", "| cell | cell |" for tables, and paragraphs.
    /// In a line, **bold**, `keys and code` and [text](#topic).
    /// </summary>
    public static partial class HelpContent
    {
        private const string Resource = "Altium_Library_Manager.Help.md";

        [GeneratedRegex(@"\*\*(?<b>.+?)\*\*|`(?<c>[^`]+)`|\[(?<t>[^\]]+)\]\(#(?<l>[a-z-]+)\)")]
        private static partial Regex SpanPattern();

        private static IReadOnlyList<HelpTopic>? _topics;

        /// <summary>The topics in the order of the help.</summary>
        public static IReadOnlyList<HelpTopic> Topics => _topics ??= Parse(ReadResource());

        private static string ReadResource()
        {
            using var stream = typeof(HelpContent).Assembly.GetManifestResourceStream(Resource)
                               ?? throw new InvalidOperationException("The help text is missing from the program.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public static HelpTopic? Find(string? id) => Topics.FirstOrDefault(t => t.Id == id);

        public static List<HelpTopic> Parse(string text)
        {
            var topics = new List<HelpTopic>();
            var group = "";
            string? id = null, title = null;
            var blocks = new List<HelpBlock>();
            var list = new List<string>();
            var rows = new List<string[]>();
            var kind = HelpBlockKind.Paragraph;

            void EndBlock()
            {
                if (rows.Count > 0)
                    blocks.Add(new HelpBlock(HelpBlockKind.Table, [], rows));
                else if (list.Count > 0)
                    blocks.Add(new HelpBlock(kind, list, []));
                (list, rows) = ([], []);
            }
            void EndTopic()
            {
                EndBlock();
                if (id != null)
                    topics.Add(new HelpTopic(id, title!, group, blocks));
                blocks = [];
            }
            void Add(HelpBlockKind lineKind, string line)
            {
                if (lineKind != kind || rows.Count > 0 || lineKind == HelpBlockKind.Paragraph)
                    EndBlock();
                kind = lineKind;
                list.Add(line);
            }

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r', ' ');
                if (line.Length == 0)
                    EndBlock();
                else if (line.StartsWith("= ", StringComparison.Ordinal))
                {
                    EndTopic();
                    (id, title) = (null, null);
                    group = line[2..].Trim();
                }
                else if (line.StartsWith("# ", StringComparison.Ordinal))
                {
                    EndTopic();
                    var parts = line[2..].Split('|', 2);
                    (id, title) = (parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : parts[0].Trim());
                }
                else if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    EndBlock();
                    blocks.Add(new HelpBlock(HelpBlockKind.Heading, [line[3..].Trim()], []));
                }
                else if (line.StartsWith("- ", StringComparison.Ordinal))
                    Add(HelpBlockKind.Bullets, line[2..].Trim());
                else if (StepPattern().Match(line) is { Success: true } step)
                    Add(HelpBlockKind.Steps, line[step.Length..].Trim());
                else if (line.StartsWith("> ", StringComparison.Ordinal))
                    Add(HelpBlockKind.Tip, line[2..].Trim());
                else if (line.StartsWith('|'))
                {
                    if (list.Count > 0)
                        EndBlock();
                    rows.Add(line.Trim('|', ' ').Split('|').Select(c => c.Trim()).ToArray());
                }
                else
                    Add(HelpBlockKind.Paragraph, line);
            }
            EndTopic();
            return topics;
        }

        [GeneratedRegex(@"^\d+\. ")]
        private static partial Regex StepPattern();

        /// <summary>A line of text cut into plain text, bold, code and links.</summary>
        public static List<HelpSpan> Spans(string line)
        {
            var spans = new List<HelpSpan>();
            var at = 0;
            foreach (Match match in SpanPattern().Matches(line))
            {
                if (match.Index > at)
                    spans.Add(new HelpSpan(HelpSpanKind.Text, line[at..match.Index]));
                if (match.Groups["b"].Success)
                    spans.Add(new HelpSpan(HelpSpanKind.Bold, match.Groups["b"].Value));
                else if (match.Groups["c"].Success)
                    spans.Add(new HelpSpan(HelpSpanKind.Code, match.Groups["c"].Value));
                else
                    spans.Add(new HelpSpan(HelpSpanKind.Link, match.Groups["t"].Value, match.Groups["l"].Value));
                at = match.Index + match.Length;
            }
            if (at < line.Length)
                spans.Add(new HelpSpan(HelpSpanKind.Text, line[at..]));
            return spans;
        }

        /// <summary>The topics a search finds, the ones with the words in their title first.</summary>
        public static List<HelpTopic> Search(string? query)
        {
            var words = Services.SearchText.Words(query);
            return words.Count == 0
                ? Topics.ToList()
                : Topics.Where(t => Services.SearchText.Matches(t.SearchText, words))
                    .OrderByDescending(t => Services.SearchText.Matches(t.Title, words)).ToList();
        }
    }
}
