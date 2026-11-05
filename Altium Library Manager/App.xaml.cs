using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Altium_Library_Manager.Services;
using Altium_Library_Manager.Services.Settings;
using Altium_Library_Manager.Services.Templates;

namespace Altium_Library_Manager
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>User settings, loaded once at startup and shared by the whole app.</summary>
        public static SettingsStore Settings { get; private set; } = null!;

        private static TemplateStore? _templates;

        /// <summary>Table templates, read from disk the first time they are needed.</summary>
        public static TemplateStore Templates
        {
            get
            {
                if (_templates == null)
                {
                    _templates = new TemplateStore(Settings.Current.Templates.Folder);
                    TemplateProblems = _templates.Load();
                }
                return _templates;
            }
        }

        /// <summary>Reads the templates again from the folder in the settings (after it was changed in Options).</summary>
        public static void ReloadTemplates() => _templates = null;

        /// <summary>Template files that couldn't be read when the templates were loaded (shown once by the templates window).</summary>
        public static List<string> TemplateProblems { get; set; } = [];

        protected override void OnStartup(StartupEventArgs e)
        {
            // An error nothing else caught is reported, logged and survived, instead of ending the app with unsaved
            // table changes in it
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                LogError("Background task", args.Exception);
                args.SetObserved();
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                // From a thread of its own: the app ends anyway, but the reason is kept
                if (args.ExceptionObject is Exception ex)
                    LogError("Fatal", ex);
            };

            // F1 anywhere: the help, at the topic about that window if there is one
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler((sender, args) =>
            {
                if (args.Key == System.Windows.Input.Key.F1 && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None &&
                    sender is Window window and not HelpWindow)
                {
                    args.Handled = true;
                    HelpWindow.Open(HelpWindow.TopicFor(window));
                }
            }));

            // Altium files store strings in Windows-1252 and similar code pages
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // The AltiumSharp renderer looks for font.ttf via a relative path
            Environment.CurrentDirectory = AppContext.BaseDirectory;

            // Before the main window is created, so it opens in the right theme without flashing
            Settings = SettingsStore.Load();
            ThemeManager.Apply(Settings.Current.Appearance.Theme);

            base.OnStartup(e);
        }

        #region Unexpected errors

        /// <summary>Where unexpected errors are written (the newest last), to find out what happened afterwards.</summary>
        public static string ErrorLogPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Altium Library Manager", "errors.log");

        private const long MaxLogSize = 1_000_000;
        private bool _reportingError;
        private readonly Queue<DateTime> _recentErrors = new();

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogError("Unexpected error", e.Exception);
            // While one is being reported: logged only
            if (_reportingError)
            {
                e.Handled = true;
                return;
            }
            // The app goes on: what failed was one action. But an error that keeps coming back (every redraw) would
            // bring up the message forever: the third within 10 seconds ends the app instead.
            var now = DateTime.Now;
            _recentErrors.Enqueue(now);
            while (_recentErrors.Count > 0 && now - _recentErrors.Peek() > TimeSpan.FromSeconds(10))
                _recentErrors.Dequeue();
            if (_recentErrors.Count >= 3)
                return;
            e.Handled = true;
            _reportingError = true;
            try
            {
                AppDialog.Error(null, "Unexpected error", "Something went wrong",
                    $"{e.Exception.Message}\n\nThe app keeps running; save your changes to be safe. " +
                    $"Details are in {ErrorLogPath}.");
            }
            catch (Exception)
            {
                // The dialog itself failed (the app is shutting down, …): the error is logged
            }
            finally
            {
                _reportingError = false;
            }
        }

        /// <summary>Appends an error with its stack trace to <see cref="ErrorLogPath"/>; the log is started afresh
        /// when it gets large. Never throws.</summary>
        public static void LogError(string what, Exception exception)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath)!);
                if (File.Exists(ErrorLogPath) && new FileInfo(ErrorLogPath).Length > MaxLogSize)
                    File.Delete(ErrorLogPath);
                File.AppendAllText(ErrorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {what}: {exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (Exception)
            {
                // Nowhere to report a failure to log
            }
        }

        #endregion
    }

}
