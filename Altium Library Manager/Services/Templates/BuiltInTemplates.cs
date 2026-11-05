using System.Collections.ObjectModel;

namespace Altium_Library_Manager.Services.Templates
{
    /// <summary>Templates added on the first start, so there is something to start from.</summary>
    public static class BuiltInTemplates
    {
        /// <summary>The fields an Altium DbLib maps by default, with Part Number as the key.</summary>
        public static TableTemplate AltiumComponent()
        {
            string[] names =
            [
                "Library Ref", "Library Path",
                "Footprint Ref", "Footprint Path", "Footprint Ref 2", "Footprint Path 2", "Footprint Ref 3", "Footprint Path 3",
                "Description", "Comment", "Value",
                "Manufacturer", "Manufacturer Part Number", "Supplier 1", "Supplier Part Number 1",
                "ComponentLink1Description", "ComponentLink1URL", "ComponentLink2Description", "ComponentLink2URL"
            ];
            var fields = new ObservableCollection<TemplateField>
            {
                new() { Name = "Part Number", Required = true, PrimaryKey = true }
            };
            foreach (var name in names)
                fields.Add(new TemplateField { Name = name });

            return new TableTemplate
            {
                Name = "Altium component",
                Description = "Part Number as the key, symbol and up to three footprints, description, manufacturer, supplier and datasheet links.",
                Fields = fields
            };
        }

        /// <summary>What "Empty table" starts with: Access tables need at least one field.</summary>
        public static TableTemplate Empty() => new()
        {
            Name = "Empty table",
            Description = "Only a Part Number field; add the others below.",
            Fields = [new TemplateField { Name = "Part Number", Required = true, PrimaryKey = true }]
        };

        /// <summary>The fields a DbLib needs to place a component: its key, symbol and footprint.</summary>
        public static TableTemplate Minimal() => new()
        {
            Name = "Minimal",
            Description = "Part Number, symbol and footprint: what Altium needs to place a component.",
            Fields = new ObservableCollection<TemplateField>(new[] { "Part Number", "Library Ref", "Library Path", "Footprint Ref", "Footprint Path" }
                .Select(name => new TemplateField { Name = name, Required = name == "Part Number", PrimaryKey = name == "Part Number" }))
        };

        /// <summary>The usual Altium DbLib fields in groups, for the "Standard fields" menu of the field editor.</summary>
        public static IReadOnlyList<string[]> StandardFields { get; } =
        [
            ["Part Number"],
            ["Library Ref", "Library Path"],
            ["Footprint Ref", "Footprint Path", "Footprint Ref 2", "Footprint Path 2", "Footprint Ref 3", "Footprint Path 3"],
            ["Description", "Comment", "Value"],
            ["Manufacturer", "Manufacturer Part Number", "Supplier 1", "Supplier Part Number 1", "Price"],
            ["ComponentLink1Description", "ComponentLink1URL", "ComponentLink2Description", "ComponentLink2URL", "HelpURL"]
        ];
    }
}
