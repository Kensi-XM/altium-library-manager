using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Altium_Library_Manager.Services.Templates
{
    /// <summary>Access field types a template can use (see <see cref="AccessDatabase.CreateTable"/> for the SQL types).</summary>
    public enum FieldType
    {
        Text,
        LongText,
        Integer,
        Number,
        Currency,
        YesNo,
        DateTime,
        AutoNumber
    }

    /// <summary>Base for the editable template classes: the dialogs bind to them directly.</summary>
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One column of a table made from a template.</summary>
    public sealed class TemplateField : Observable
    {
        public const int DefaultTextSize = 255;

        private string _name = "";
        private FieldType _type = FieldType.Text;
        private int _size = DefaultTextSize;
        private bool _required;
        private bool _primaryKey;

        public string Name { get => _name; set => Set(ref _name, value ?? ""); }

        public FieldType Type
        {
            get => _type;
            set
            {
                Set(ref _type, value);
                Raise(nameof(HasSize));
            }
        }

        /// <summary>Maximum length in characters; only Text fields have one (1–255).</summary>
        public int Size { get => _size; set => Set(ref _size, value); }

        public bool Required { get => _required; set => Set(ref _required, value); }

        /// <summary>Part of the table's primary key (Part Number, usually); key fields are always required.</summary>
        public bool PrimaryKey
        {
            get => _primaryKey;
            set
            {
                Set(ref _primaryKey, value);
                if (value)
                    Required = true;
            }
        }

        [JsonIgnore]
        public bool HasSize => Type == FieldType.Text;

        public TemplateField Clone() => new() { Name = Name, Type = Type, Size = Size, Required = Required, PrimaryKey = PrimaryKey };
    }

    /// <summary>A reusable set of fields for new tables, stored as a JSON file (see <see cref="TemplateStore"/>).</summary>
    public sealed class TableTemplate : Observable
    {
        private string _name = "";
        private string _description = "";

        public string Name { get => _name; set => Set(ref _name, value ?? ""); }

        public string Description { get => _description; set => Set(ref _description, value ?? ""); }

        private ObservableCollection<TemplateField> _fields = [];

        public ObservableCollection<TemplateField> Fields
        {
            get => _fields;
            set
            {
                Set(ref _fields, value ?? []);
                Raise(nameof(FieldCount));
            }
        }

        /// <summary>"1 field", "20 fields" (for the template lists).</summary>
        [JsonIgnore]
        public string FieldCount => Fields.Count == 1 ? "1 field" : $"{Fields.Count} fields";

        // Screen readers and UI Automation read list items by their text
        public override string ToString() => Name;

        public TableTemplate Clone() => new()
        {
            Name = Name,
            Description = Description,
            Fields = new ObservableCollection<TemplateField>(Fields.Select(f => f.Clone()))
        };
    }
}
