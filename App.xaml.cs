// App.xaml.cs + AppShell.xaml.cs
// Both classes in one file. All using directives are at the top.

using EduAutomation.Services;
using EduAutomation.Views;
using Microsoft.Maui.Controls;

namespace EduAutomation
{
    public partial class App : Application
    {
        private readonly ISecureConfigService _config;
        private readonly ILoggingService _log;

        public App(ISecureConfigService config, ILoggingService log)
        {
            InitializeComponent();
            _config = config;
            _log = log;
            MainPage = new AppShell();
        }

        protected override async void OnStart()
        {
            base.OnStart();
            _log.LogInfo("App", "Application started.");
            bool isConfigured = await _config.IsConfigurationCompleteAsync();
            if (!isConfigured)
            {
                _log.LogInfo("App", "First run detected. Navigating to Settings page.");
                await Shell.Current.GoToAsync("//settings");
            }
        }

        protected override void OnSleep()
        {
            _log.LogInfo("App", "Application entering background.");
        }

        protected override void OnResume()
        {
            _log.LogInfo("App", "Application resumed from background.");
        }
    }

    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();
            Routing.RegisterRoute("settings", typeof(SettingsPage));
            Routing.RegisterRoute("dashboard", typeof(DashboardPage));
            Routing.RegisterRoute("gmail", typeof(GmailPage));
            Routing.RegisterRoute("assignments", typeof(AssignmentsPage));
            Routing.RegisterRoute("datadump", typeof(DataDumpPage));
            Routing.RegisterRoute("review", typeof(ReviewPage));
        }
    }
}