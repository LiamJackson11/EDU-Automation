// App.xaml.cs + AppShell code-behind
// Both partial classes live here. All using directives at the top.

using EduAutomation.Services;
using EduAutomation.Views;

namespace EduAutomation
{
    public partial class App : Application
    {
        private readonly ISecureConfigService _config;
        private readonly ILoggingService      _log;

        public App(ISecureConfigService config, ILoggingService log)
        {
            InitializeComponent();
            _config  = config;
            _log     = log;
            MainPage = new AppShell();
        }

        protected override async void OnStart()
        {
            base.OnStart();
            _log.LogInfo("App", "Application started.");

            // On first launch (no credentials stored), send user to Settings.
            // Use relative route "settings" — NOT "//settings".
            // "//route" is only valid for TabBar ShellContent routes.
            bool isConfigured = await _config.IsConfigurationCompleteAsync();
            if (!isConfigured)
            {
                _log.LogInfo("App",
                    "First run or incomplete configuration detected. " +
                    "Navigating to Settings.");
                await Shell.Current.GoToAsync("settings");
            }
        }

        protected override void OnSleep()  =>
            _log.LogInfo("App", "Application entering background.");

        protected override void OnResume() =>
            _log.LogInfo("App", "Application resumed from background.");
    }

    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();

            // Register all non-tab pages as navigable routes.
            // Tab routes (dashboard, gmail, assignments, datadump, review) are
            // declared in AppShell.xaml inside the <TabBar> and do not need
            // registration here.
            Routing.RegisterRoute("settings",    typeof(SettingsPage));
        }
    }
}