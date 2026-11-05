namespace Altium_Library_Manager.Services.Templates
{
    /// <summary>
    /// Checks names and field lists against Access's rules before anything is written, so the user gets a clear
    /// message instead of an OLE DB error after the fact.
    /// </summary>
    public static class TemplateRules
    {
        public const int MaxNameLength = 64;
        public const int MaxFields = 255;

        private static readonly char[] ForbiddenChars = ['.', '!', '`', '[', ']'];

        /// <summary>Why the name can't be used for an Access table or field, or null if it can.</summary>
        public static string? CheckName(string? name, string what)
        {
            if (string.IsNullOrWhiteSpace(name))
                return $"{what} name is empty.";
            if (name.Length > MaxNameLength)
                return $"{what} name \"{name}\" is longer than {MaxNameLength} characters.";
            if (name[0] == ' ' || name[^1] == ' ')
                return $"{what} name \"{name}\" starts or ends with a space.";
            if (name.Any(char.IsControl))
                return $"{what} name \"{name}\" contains a control character.";
            var forbidden = name.IndexOfAny(ForbiddenChars);
            if (forbidden >= 0)
                return $"{what} name \"{name}\" contains \"{name[forbidden]}\", which Access doesn't allow (. ! ` [ ]).";
            return null;
        }

        /// <summary>All the problems of a field list; empty when a table can be made from it.</summary>
        public static List<string> CheckFields(IReadOnlyCollection<TemplateField> fields)
        {
            var errors = new List<string>();
            if (fields.Count == 0)
                errors.Add("Add at least one field.");
            if (fields.Count > MaxFields)
                errors.Add($"Access tables have at most {MaxFields} fields.");

            foreach (var field in fields)
            {
                if (CheckName(field.Name, "Field") is { } nameError)
                    errors.Add(nameError);
                if (!Enum.IsDefined(field.Type))
                    errors.Add($"Field \"{field.Name}\": unknown type.");
                if (field.Type == FieldType.Text && field.Size is < 1 or > 255)
                    errors.Add($"Field \"{field.Name}\": the size of a Text field is 1 to 255 characters.");
                if (field.PrimaryKey && field.Type == FieldType.LongText)
                    errors.Add($"Field \"{field.Name}\": a Long text field can't be part of the primary key.");
            }

            foreach (var duplicate in fields.GroupBy(f => f.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Key.Length > 0 && g.Count() > 1))
                errors.Add($"Field name \"{duplicate.Key}\" is used more than once.");

            if (fields.Count(f => f.Type == FieldType.AutoNumber) > 1)
                errors.Add("A table can have only one AutoNumber field.");
            return errors;
        }
    }
}
