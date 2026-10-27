using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;

namespace TeselaSettings
{
    public partial class App : Application
    {
        Window window;

        public App()
        {
            // El tema (claro u oscuro) es el de Tesela; se fija antes de crear nada
            RequestedTheme = TeselaIsDark() ? ApplicationTheme.Dark : ApplicationTheme.Light;
            InitializeComponent();
        }

        /// <summary>Tema de Tesela (state.json): "dark", "light" o "auto" (el de las apps de Windows).</summary>
        internal static bool TeselaIsDark()
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tesela", "state.json");
                var theme = JsonNode.Parse(File.ReadAllText(path))?["Theme"]?.GetValue<string>();
                if (theme == "dark") return true;
                if (theme == "light") return false;
            }
            catch { }
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return !(key?.GetValue("AppsUseLightTheme") is int light && light == 1);
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            window = new MainWindow();
            window.Activate();
        }
    }
}
