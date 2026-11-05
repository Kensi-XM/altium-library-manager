using System.Windows;
using System.Windows.Media;
using Altium_Library_Manager.Services.Settings;
using Microsoft.Win32;

namespace Altium_Library_Manager.Services
{
    /// <summary>
    /// Light / dark appearance. Standard controls use WPF's built-in Fluent theme (Application.ThemeMode); the app's
    /// own colors are resource brushes (referenced with DynamicResource) replaced here, and parts that aren't WPF
    /// (the 3D viewer, footprint rendering) follow <see cref="EffectiveThemeChanged"/>.
    /// </summary>
    public static class ThemeManager
    {
        // Dark is a soft dark gray rather than black, close to Fluent's own dark surfaces
        private static readonly Dictionary<string, (Color Light, Color Dark)> Brushes = new()
        {
            // Solid window background: with the custom title bar the Fluent backdrop (Mica) no longer shows through
            ["WindowBackgroundBrush"] = (Color.FromRgb(0xF3, 0xF3, 0xF3), Color.FromRgb(0x20, 0x20, 0x20)),
            ["PanelHeaderBrush"] = (Color.FromRgb(0xED, 0xEE, 0xF0), Color.FromRgb(0x2E, 0x30, 0x34)),
            ["PanelBorderBrush"] = (Color.FromRgb(0xD5, 0xD7, 0xDB), Color.FromRgb(0x43, 0x46, 0x4B)),
            ["ContentBackgroundBrush"] = (Colors.White, Color.FromRgb(0x25, 0x26, 0x29)),
            // Every other table row (Options → Tables), a shade off the content background
            ["AlternateRowBrush"] = (Color.FromRgb(0xF3, 0xF4, 0xF6), Color.FromRgb(0x2E, 0x2F, 0x33)),
            ["MutedTextBrush"] = (Color.FromRgb(0x80, 0x80, 0x80), Color.FromRgb(0x9A, 0x9C, 0xA0)),
            ["PrimaryTextBrush"] = (Color.FromRgb(0x1B, 0x1B, 0x1B), Color.FromRgb(0xE4, 0xE5, 0xE7)),
            ["IconForegroundBrush"] = (Color.FromRgb(0x44, 0x46, 0x49), Color.FromRgb(0xC9, 0xCB, 0xCF)),
            ["IconHoverBrush"] = (Color.FromRgb(0xDD, 0xDF, 0xE3), Color.FromRgb(0x3C, 0x3F, 0x44)),
            ["IconPressedBrush"] = (Color.FromRgb(0xCB, 0xCE, 0xD3), Color.FromRgb(0x4A, 0x4D, 0x53)),
            // Title bar buttons: translucent, like the Windows caption buttons, so they suit any window background
            ["CaptionHoverBrush"] = (Color.FromArgb(0x18, 0, 0, 0), Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            ["CaptionPressedBrush"] = (Color.FromArgb(0x10, 0, 0, 0), Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            ["CaptionHoverForegroundBrush"] = (Color.FromRgb(0x1B, 0x1B, 0x1B), Color.FromRgb(0xE4, 0xE5, 0xE7))
        };

        private static AppTheme _theme;
        private static bool _listeningToSystem;

        /// <summary>Whether the app is currently dark (for <see cref="AppTheme.System"/>: what Windows uses).</summary>
        public static bool IsDark { get; private set; }

        public static AppTheme Theme => _theme;

        public static event EventHandler? EffectiveThemeChanged;

        public static void Apply(AppTheme theme)
        {
            _theme = theme;

#pragma warning disable WPF0001 // ThemeMode is marked experimental, but it is the supported way to get Fluent light/dark
            Application.Current.ThemeMode = theme switch
            {
                AppTheme.Light => ThemeMode.Light,
                AppTheme.Dark => ThemeMode.Dark,
                _ => ThemeMode.System
            };
#pragma warning restore WPF0001

            if (theme == AppTheme.System && !_listeningToSystem)
            {
                SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
                _listeningToSystem = true;
            }

            UpdateEffectiveTheme();
        }

        private static void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // Raised on a system thread; the Windows light/dark switch arrives as a General change
            if (_theme == AppTheme.System && e.Category == UserPreferenceCategory.General)
                Application.Current?.Dispatcher.BeginInvoke(UpdateEffectiveTheme);
        }

        private static void UpdateEffectiveTheme()
        {
            var dark = _theme == AppTheme.Dark || _theme == AppTheme.System && WindowsAppsUseDarkMode();
            foreach (var (key, (light, darkColor)) in Brushes)
            {
                var brush = new SolidColorBrush(dark ? darkColor : light);
                brush.Freeze();
                Application.Current.Resources[key] = brush;
            }

            // Selected grid rows: a light tint of the Windows accent color with the normal text color. Fluent's own
            // selection is a dark accent fill with accent-colored text, hard to read. App resources take precedence
            // over the Fluent dictionaries, so these keys replace the ones its DataGrid templates use.
            var accent = SystemColors.AccentColor;
            var selection = new SolidColorBrush(Color.FromArgb(dark ? (byte)0x50 : (byte)0x30, accent.R, accent.G, accent.B));
            selection.Freeze();
            Application.Current.Resources["DataGridRowSelectedBackgroundThemeBrush"] = selection;
            // The current row of a table: a fainter tint, which selected cells stand out on (their tint adds to it)
            var currentRow = new SolidColorBrush(Color.FromArgb(dark ? (byte)0x28 : (byte)0x18, accent.R, accent.G, accent.B));
            currentRow.Freeze();
            Application.Current.Resources["CurrentRowBrush"] = currentRow;
            Application.Current.Resources["DataGridRowSelectedForegroundThemeBrush"] = Application.Current.Resources["PrimaryTextBrush"];

            IsDark = dark;
            EffectiveThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        private static bool WindowsAppsUseDarkMode()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
    }
}
