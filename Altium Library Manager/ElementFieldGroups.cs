using System.Text.RegularExpressions;
using System.Windows;
using Altium_Library_Manager.Services;

namespace Altium_Library_Manager
{
    /// <summary>A field in a row of the Fields list, with the short name shown next to its box.</summary>
    public sealed record FieldCell(ElementField Field, string Label)
    {
        public bool HasLabel => Label.Length > 0;
    }

    /// <summary>
    /// A row of the Fields list: one field, or two that belong together (a footprint's Ref and Path, a link's
    /// description and address) side by side.
    /// </summary>
    public sealed record FieldRow(string Label, FieldCell First, FieldCell? Second, double FirstShare = 2, double SecondShare = 3)
    {
        public string Tip => Second == null ? First.Field.Name : $"{First.Field.Name} and {Second.Field.Name}";
        public GridLength FirstWidth => new(Second == null ? 1 : FirstShare, GridUnitType.Star);
        public GridLength GapWidth => new(Second == null ? 0 : 10);
        public GridLength SecondWidth => Second == null ? new GridLength(0) : new GridLength(SecondShare, GridUnitType.Star);
    }

    public sealed record FieldGroup(string Title, IReadOnlyList<FieldRow> Rows);

    /// <summary>
    /// Puts the fields of Add elements in groups, in the order one fills them: the part itself, its symbol and
    /// footprints, who makes and sells it, its documents, then the rest. Fields that belong together share a row, so
    /// a footprint's Ref sits next to its Path. Works by the field names (spaces and case ignored), so any table works;
    /// a field that fits no group is under Other.
    /// </summary>
    public static partial class ElementFieldGroups
    {
        [GeneratedRegex(@"^ComponentLink(\d+)(Description|URL)$", RegexOptions.IgnoreCase)]
        private static partial Regex LinkName();

        [GeneratedRegex(@"^(Manufacturer|Supplier)(PartNumber)?(\d*)$", RegexOptions.IgnoreCase)]
        private static partial Regex MakerName();

        [GeneratedRegex(@"(\d+)$")]
        private static partial Regex Number();

        private static string Compact(string name) => name.Replace(" ", "").Replace("_", "");

        private static int NumberOf(string name) => Number().Match(Compact(name)) is { Success: true } m && int.TryParse(m.Value, out var n) ? n : 1;

        private static string Numbered(string label, int number) => number == 1 ? label : $"{label} {number}";

        public static List<FieldGroup> Build(IReadOnlyList<ElementField> fields, string? partNumberField, string? valueField)
        {
            var left = fields.ToList();
            ElementField? Take(Func<ElementField, bool> match)
            {
                var field = left.FirstOrDefault(match);
                if (field != null)
                    left.Remove(field);
                return field;
            }
            bool Named(ElementField f, string name) => string.Equals(Compact(f.Name), Compact(name), StringComparison.OrdinalIgnoreCase);
            FieldRow Single(ElementField field) => new(field.Name, new FieldCell(field, ""), null);

            // The part itself: what changes from row to row
            var main = new List<FieldRow>();
            foreach (var name in new[] { partNumberField, valueField, "Comment", "Description" })
                if (name != null && Take(f => Named(f, name)) is { } field)
                    main.Add(Single(field));

            // Symbol and footprints: each Ref with its Path
            var library = new List<FieldRow>();
            if (Take(f => f.Link == FieldLink.SymbolRef) is { } symbolRef)
                library.Add(Take(f => f.Link == FieldLink.Symbol) is { } symbolPath
                    ? new FieldRow("Symbol", new FieldCell(symbolRef, "Ref"), new FieldCell(symbolPath, "Path"))
                    : Single(symbolRef));
            else if (Take(f => f.Link == FieldLink.Symbol) is { } lonePath)
                library.Add(Single(lonePath));
            foreach (var footprintRef in left.Where(f => f.Link == FieldLink.FootprintRef).ToList())
            {
                left.Remove(footprintRef);
                var number = NumberOf(footprintRef.Name);
                library.Add(Take(f => f.Link == FieldLink.Footprint && NumberOf(f.Name) == number) is { } path
                    ? new FieldRow(Numbered("Footprint", number), new FieldCell(footprintRef, "Ref"), new FieldCell(path, "Path"))
                    : Single(footprintRef));
            }
            foreach (var path in left.Where(f => f.Link == FieldLink.Footprint).ToList())
            {
                left.Remove(path);
                library.Add(Single(path));
            }

            // Who makes and sells it: each name with its part number
            var makers = new List<FieldRow>();
            foreach (var field in left.Where(f => MakerName().Match(Compact(f.Name)) is { Success: true } m && !m.Groups[2].Success).ToList())
            {
                left.Remove(field);
                var m = MakerName().Match(Compact(field.Name));
                var (kind, number) = (m.Groups[1].Value, m.Groups[3].Value);
                var partNumber = Take(f => MakerName().Match(Compact(f.Name)) is { Success: true } p && p.Groups[2].Success &&
                                           string.Equals(p.Groups[1].Value, kind, StringComparison.OrdinalIgnoreCase) && p.Groups[3].Value == number);
                makers.Add(partNumber != null
                    ? new FieldRow(field.Name, new FieldCell(field, "Name"), new FieldCell(partNumber, "Part number"), 1, 1)
                    : Single(field));
            }
            foreach (var field in left.Where(f => MakerName().IsMatch(Compact(f.Name))).ToList())
            {
                left.Remove(field);
                makers.Add(Single(field));
            }

            // Documents: each link's description with its address
            var links = new List<FieldRow>();
            foreach (var description in left.Where(f => LinkName().Match(Compact(f.Name)) is { Success: true } m &&
                                                        m.Groups[2].Value.Equals("Description", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                left.Remove(description);
                var number = LinkName().Match(Compact(description.Name)).Groups[1].Value;
                links.Add(Take(f => LinkName().Match(Compact(f.Name)) is { Success: true } m && m.Groups[1].Value == number &&
                                    m.Groups[2].Value.Equals("URL", StringComparison.OrdinalIgnoreCase)) is { } url
                    ? new FieldRow($"Link {number}", new FieldCell(description, "Text"), new FieldCell(url, "URL"), 1, 3)
                    : Single(description));
            }
            foreach (var url in left.Where(f => LinkName().IsMatch(Compact(f.Name)) || f.Link == FieldLink.Document).ToList())
            {
                left.Remove(url);
                links.Add(Single(url));
            }

            // A field holding a placeholder changes from row to row too: it goes with the part's own fields
            foreach (var field in left.Where(f => f.Pattern.Contains('{')).ToList())
            {
                left.Remove(field);
                main.Add(Single(field));
            }

            return new List<FieldGroup>
            {
                new("Part", main),
                new("Symbol and footprints", library),
                new("Manufacturer and supplier", makers),
                new("Links", links),
                new("Other fields", left.Select(Single).ToList())
            }.Where(g => g.Rows.Count > 0).ToList();
        }
    }
}
