using System.Windows;
using System.Windows.Controls;

namespace Altium_Library_Manager
{
    /// <summary>Own title bar of the dialogs (icon glyph, title, close), matching the main window's.</summary>
    public partial class DialogTitleBar : UserControl
    {
        public static readonly DependencyProperty IconProperty =
            DependencyProperty.Register(nameof(Icon), typeof(string), typeof(DialogTitleBar));

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(DialogTitleBar));

        public DialogTitleBar()
        {
            InitializeComponent();
        }

        /// <summary>A Segoe Fluent Icons glyph.</summary>
        public string Icon
        {
            get => (string)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
    }
}
