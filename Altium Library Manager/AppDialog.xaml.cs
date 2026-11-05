using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Altium_Library_Manager
{
    public enum DialogKind
    {
        Info,
        Question,
        Warning,
        Error
    }

    /// <summary>
    /// The app's message box, in the app's colors and following its theme (the Windows one is always light),
    /// with buttons named after what they do ("Delete", "Keep both") instead of Yes / No / Cancel.
    /// </summary>
    public partial class AppDialog : Window
    {
        private int _result;

        private AppDialog(DialogKind kind, string title, string message, string? detail, IReadOnlyList<string> buttons,
            bool destructive = false, bool focusAction = false)
        {
            InitializeComponent();
            // Not shown in the window (the message says it all), but named in Alt+Tab and for screen readers
            Title = title;
            MessageText.Text = message;
            DetailText.Text = detail;
            DetailScroll.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;

            (KindIcon.Text, var brush) = kind switch
            {
                DialogKind.Warning => ("", "SystemFillColorCautionBrush"),
                DialogKind.Error => ("", "SystemFillColorCriticalBrush"),
                DialogKind.Question => ("", "AccentTextFillColorPrimaryBrush"),
                _ => ("", "AccentTextFillColorPrimaryBrush")
            };
            KindIcon.SetResourceReference(TextBlock.ForegroundProperty, brush);

            // The first button is the main one (Enter); the last one, or the only one, is what Esc and the close button mean.
            // For a destructive action the main button is red, and Enter means the last one (Cancel), so the action takes
            // a deliberate click; unless the action can be undone elsewhere (focusAction), then Enter goes ahead
            _result = buttons.Count - 1;
            var focused = destructive && !focusAction ? buttons.Count - 1 : 0;
            for (var i = 0; i < buttons.Count; i++)
            {
                var index = i;
                var button = new Button
                {
                    Content = buttons[i],
                    MinWidth = 96,
                    Padding = new Thickness(14, 4, 14, 4),
                    Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                    IsDefault = i == focused,
                    IsCancel = i == buttons.Count - 1
                };
                if (i == 0)
                {
                    button.SetResourceReference(StyleProperty, "AccentButtonStyle");
                    if (destructive)
                    {
                        // The red of the title bar close button, in every state of the accent button
                        // (the theme's critical brush is a pale pink in the dark theme, unreadable under white text)
                        foreach (var (key, color) in new[]
                                 {
                                     ("AccentButtonBackground", Color.FromRgb(0xC4, 0x2B, 0x1C)),
                                     ("AccentButtonBackgroundPointerOver", Color.FromRgb(0xD0, 0x3E, 0x30)),
                                     ("AccentButtonBackgroundPressed", Color.FromRgb(0xA8, 0x24, 0x18)),
                                     ("AccentButtonForeground", Colors.White),
                                     ("AccentButtonForegroundPointerOver", Colors.White),
                                     ("AccentButtonForegroundPressed", Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF))
                                 })
                            button.Resources[key] = new SolidColorBrush(color);
                    }
                }
                button.Click += (_, _) =>
                {
                    _result = index;
                    Close();
                };
                Buttons.Children.Add(button);
            }
            Loaded += (_, _) => ((Button)Buttons.Children[focused]).Focus();
            // The window sizes itself to its content; when the app is busy right then (a table loading), the first frame
            // could show the window before its content was laid out (an empty box with one button). Lay it out and draw
            // it again once it is on screen.
            ContentRendered += (_, _) =>
            {
                InvalidateMeasure();
                InvalidateVisual();
            };
        }

        /// <summary>Shows the dialog and returns the index of the button chosen (Esc / close: the last one).</summary>
        public static int Show(Window? owner, DialogKind kind, string title, string message, string? detail, params string[] buttons) =>
            Show(owner, kind, title, message, detail, false, false, buttons);

        /// <summary>Set by the automated tests: gets the title, message, detail and buttons instead of a window appearing on the
        /// screen, and answers with the index of the button chosen.</summary>
        public static Func<string, string, string?, string[], int>? Instead;

        private static int Show(Window? owner, DialogKind kind, string title, string message, string? detail, bool destructive,
            bool focusAction, string[] buttons)
        {
            if (Instead is { } answer)
                return answer(title, message, detail, buttons);
            var dialog = new AppDialog(kind, title, message, detail, buttons.Length > 0 ? buttons : ["OK"], destructive, focusAction);
            return dialog.Run(owner);
        }

        private int Run(Window? owner)
        {
            owner ??= Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
            if (owner is { IsLoaded: true } && owner != this)
                Owner = owner;
            else
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowDialog();
            return _result;
        }

        /// <summary>Asks for a line of text, starting from <paramref name="value"/>. Null when cancelled.</summary>
        public static string? Prompt(Window? owner, string title, string message, string value, string action)
        {
            if (Instead is { } answer)
                return answer(title, message, null, [action, "Cancel"]) == 0 ? value : null;
            var dialog = new AppDialog(DialogKind.Question, title, message, null, [action, "Cancel"]);
            dialog.InputBox.Text = value;
            dialog.InputBox.Visibility = Visibility.Visible;
            dialog.Loaded += (_, _) =>
            {
                dialog.InputBox.Focus();
                dialog.InputBox.SelectAll();
            };
            return dialog.Run(owner) == 0 ? dialog.InputBox.Text : null;
        }

        /// <summary>Asks to go ahead with an action; true when <paramref name="action"/> (e.g. "Delete") was chosen.</summary>
        public static bool Confirm(Window? owner, string title, string message, string action, string? detail = null,
            DialogKind kind = DialogKind.Question) =>
            Show(owner, kind, title, message, detail, action, "Cancel") == 0;

        /// <summary>
        /// Asks before a destructive action: warning icon and a red <paramref name="action"/> button. Enter cancels,
        /// unless <paramref name="focusAction"/> is set (for actions that can be undone, like deleting a table, which is
        /// kept in the bin). True when the action was chosen.
        /// </summary>
        public static bool ConfirmDestructive(Window? owner, string title, string message, string action, string? detail = null,
            bool focusAction = false) =>
            Show(owner, DialogKind.Warning, title, message, detail, true, focusAction, [action, "Cancel"]) == 0;

        public static void Error(Window? owner, string title, string message, string? detail = null) =>
            Show(owner, DialogKind.Error, title, message, detail, "OK");

        public static void Warning(Window? owner, string title, string message, string? detail = null) =>
            Show(owner, DialogKind.Warning, title, message, detail, "OK");
    }
}
