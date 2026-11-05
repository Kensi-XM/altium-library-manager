using System.IO;
using System.Text.RegularExpressions;

namespace Altium_Library_Manager.Services
{
    /// <summary>What a field's value points to, going by the field's name.</summary>
    public enum FieldLink
    {
        None,
        /// <summary>Library Path: a schematic library.</summary>
        Symbol,
        /// <summary>Footprint Path, Footprint Path 2, …: a PCB library.</summary>
        Footprint,
        /// <summary>ComponentLink1URL, …: a web address or a document (usually a datasheet).</summary>
        Document,
        /// <summary>Any other path.</summary>
        File,
        /// <summary>Library Ref: a symbol in the library of Library Path.</summary>
        SymbolRef,
        /// <summary>Footprint Ref, Footprint Ref 2, …: a footprint in the library of its Footprint Path.</summary>
        FootprintRef
    }

    /// <summary>
    /// The fields of a row that point to a file or a web page: how to pick a file for them, how to store it, and what
    /// opening their value may do. Values come from the database, so they are never run as they are: only web
    /// addresses and known document types are handed to Windows to open, anything else is just shown in its folder.
    /// </summary>
    public static partial class FieldLinks
    {
        private static readonly HashSet<string> DocumentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".odt", ".ods", ".rtf", ".txt",
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"
        };

        // Whole names (spaces ignored): "ref" alone is too common a part of other words
        [GeneratedRegex(@"^(library|lib|symbol)ref$", RegexOptions.IgnoreCase)]
        private static partial Regex SymbolRefName();

        [GeneratedRegex(@"^footprintref(?<n>\d*)$", RegexOptions.IgnoreCase)]
        private static partial Regex FootprintRefName();

        public static bool IsRef(FieldLink link) => link is FieldLink.SymbolRef or FieldLink.FootprintRef;

        /// <summary>The fields that may hold the library of a Ref field, in order: a footprint's own path, then the main
        /// one (an alternative without its own path is in the main footprint's library).</summary>
        public static IReadOnlyList<string> LibraryFields(string field, FieldLink link)
        {
            if (link == FieldLink.SymbolRef)
                return ["Library Path", "LibPath", "Symbol Path"];
            var number = FootprintRefName().Match(field.Replace(" ", "")).Groups["n"].Value;
            return number.Length > 0
                ? [$"Footprint Path {number}", "Footprint Path", "Footprint Path 1"]
                : ["Footprint Path", "Footprint Path 1"];
        }

        public static FieldLink Classify(string field)
        {
            var compact = field.Replace(" ", "");
            if (SymbolRefName().IsMatch(compact))
                return FieldLink.SymbolRef;
            if (FootprintRefName().IsMatch(compact))
                return FieldLink.FootprintRef;
            if (field.Contains("url", StringComparison.OrdinalIgnoreCase))
                return FieldLink.Document;
            if (!field.Contains("path", StringComparison.OrdinalIgnoreCase))
                return FieldLink.None;
            if (field.Contains("footprint", StringComparison.OrdinalIgnoreCase) || field.Contains("pcb", StringComparison.OrdinalIgnoreCase))
                return FieldLink.Footprint;
            if (field.Contains("library", StringComparison.OrdinalIgnoreCase) || field.Contains("lib", StringComparison.OrdinalIgnoreCase) ||
                field.Contains("symbol", StringComparison.OrdinalIgnoreCase))
                return FieldLink.Symbol;
            return FieldLink.File;
        }

        /// <summary>The file dialog's filter: the usual type first, all files always available.</summary>
        public static string Filter(FieldLink link) => link switch
        {
            FieldLink.Symbol => "Schematic libraries (*.SchLib)|*.SchLib|All files|*.*",
            FieldLink.Footprint => "PCB libraries (*.PcbLib)|*.PcbLib|All files|*.*",
            FieldLink.Document => "Documents (*.pdf;*.doc;*.docx)|*.pdf;*.doc;*.docx|All files|*.*",
            _ => "All files|*.*"
        };

        public static string BrowseTip(FieldLink link) => link switch
        {
            FieldLink.Symbol => "Choose a schematic library (needs Edit, Ctrl+E)",
            FieldLink.Footprint => "Choose a PCB library (needs Edit, Ctrl+E)",
            FieldLink.Document => "Choose a document (needs Edit, Ctrl+E)",
            FieldLink.SymbolRef => "Choose a symbol from the library in Library Path (needs Edit, Ctrl+E)",
            FieldLink.FootprintRef => "Choose a footprint from the library in its Footprint Path (needs Edit, Ctrl+E)",
            _ => "Choose a file (needs Edit, Ctrl+E)"
        };

        /// <summary>
        /// A picked library as the field stores it: relative to the database folder when it is inside it (as the
        /// paths in the database are, so the folder can move), with the slashes of <paramref name="example"/>, a
        /// path already in the row; otherwise the full path.
        /// </summary>
        public static string StoredPath(string file, string databaseFolder, string? example)
        {
            var relative = Path.GetRelativePath(databaseFolder, file);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(@"..\", StringComparison.Ordinal))
                return file;
            return example != null && example.Contains('/') && !example.Contains('\\') ? relative.Replace('\\', '/') : relative;
        }

        /// <summary>The web address in a value (http or https only; "www." is taken as https), or null. Other schemes
        /// (which can start programs) are never opened.</summary>
        public static string? WebAddress(string value)
        {
            var text = value.Trim();
            if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                text = "https://" + text;
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
        }

        /// <summary>The path in a value: as it is, or from a file:// address.</summary>
        public static string LocalPath(string value)
        {
            var text = value.Trim();
            return text.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsFile
                ? uri.LocalPath
                : text;
        }

        /// <summary>A value that has something to open: a path or link field's, or any web address.</summary>
        public static bool CanOpen(string value, FieldLink link) =>
            value.Trim().Length > 0 && !IsRef(link) && (link != FieldLink.None || WebAddress(value) != null);

        /// <summary>A file safe to open with its program. Anything else (a program, a script, a library) is shown in
        /// its folder instead.</summary>
        public static bool IsDocument(string path) => DocumentTypes.Contains(Path.GetExtension(path));
    }
}
