using System.IO;
using System.Windows;
using System.Windows.Threading;
using Altium_Library_Manager.Services;
using Altium_Library_Manager.Services.Settings;

namespace Altium_Library_Manager.Tests
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var sessions = args.Length > 0 && int.TryParse(args[0], out var count) ? count : 12;
            var steps = args.Length > 1 && int.TryParse(args[1], out var length) ? length : 300;

            T.WatchForHangs(120);
            TableTests.AnswerDialogs();
            LogicTests.Run();
            HelpTests.RunText();
            DatabaseTests.Run();
            SettingsTests.RunFiles();

            // A WPF app without its main window or the user's settings: WPF's own startup opens an empty window instead,
            // and everything then runs on settings in the temp folder
            var folder = Path.Combine(Path.GetTempPath(), "alm-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var blank = Path.Combine(folder, "blank.xaml");
            File.WriteAllText(blank, "<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Width=\"1\" Height=\"1\" Left=\"-3000\" ShowInTaskbar=\"False\" ShowActivated=\"False\" />");
            var app = new App();
            app.InitializeComponent();
            app.StartupUri = new Uri(blank);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            T.Wait(300);
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var store = SettingsStore.Load(Path.Combine(folder, "settings.json"));
            typeof(App).GetProperty("Settings")!.SetValue(null, store);
            ThemeManager.Apply(AppTheme.Dark);

            // The window sets up the styles of the grids; no database is open
            store.Current.General.RestoreSession = false;
            var window = new MainWindow { ShowActivated = false, Left = -3000 };

            TableTests.Run();
            SortTests.Run();
            DefaultTests.Run();
            SettingsTests.Run();
            HelpTests.RunWindow();
            StressTests.Run(sessions, steps);

            window.Close();
            return T.Report();
        }
    }
}
