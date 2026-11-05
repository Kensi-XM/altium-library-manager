using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Altium_Library_Manager.Services.Settings;

namespace Altium_Library_Manager
{
    /// <summary>The app's version and what it runs on.</summary>
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            VersionText.Text = $"Version {AppVersion}";
            DetailsText.Text = string.Join("\n", Details());
        }

        public static string AppVersion => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "unknown";

        private static string Component(string assembly)
        {
            try
            {
                return Assembly.Load(assembly).GetName().Version?.ToString(3) ?? "unknown";
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException)
            {
                return "not found";
            }
        }

        public static IEnumerable<string> Details()
        {
            yield return $".NET: {Environment.Version}";
            yield return $"Windows: {Environment.OSVersion.Version} ({RuntimeInformation.ProcessArchitecture})";
            yield return $"Library reader: AltiumSharp {Component("OriginalCircuit.AltiumSharp")}";
            yield return $"Database access: System.Data.OleDb {Component("System.Data.OleDb")}";
            yield return $"3D view: WebView2 {Component("Microsoft.Web.WebView2.Core")}";
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText($"Altium Library Manager {AppVersion}\n{DetailsText.Text}");
            }
            catch (ExternalException)
            {
                // Another program has the clipboard open
                AppDialog.Error(this, "About", "The clipboard is busy", "Try again in a moment.");
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = Path.GetDirectoryName(SettingsStore.DefaultPath)!;
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder } });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                AppDialog.Error(this, "About", "Couldn't open the folder", ex.Message);
            }
        }
    }
}
