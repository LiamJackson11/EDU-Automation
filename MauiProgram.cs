// MauiProgram.cs
// DI container, HttpClient factory with CookieContainer handlers,
// and all service/viewmodel registrations.

using System.Net;
using EduAutomation.Services;
using EduAutomation.ViewModels;
using EduAutomation.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace EduAutomation
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();

            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf",   "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf",  "OpenSansSemibold");
                });

            // ---- Serilog ----
            var serilog = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Debug()
                .CreateLogger();

            builder.Logging.ClearProviders().AddSerilog(serilog);

            // ---- Core singleton services ----
            builder.Services.AddSingleton<ILoggingService,      LoggingService>();
            builder.Services.AddSingleton<ISecureConfigService, SecureConfigService>();

            // ---- HttpClients ----
            // Each scraping client owns its own CookieContainer so sessions are
            // completely isolated. AllowAutoRedirect=true ensures login redirects
            // are followed transparently.

            builder.Services.AddSingleton<ICanvasService>(sp =>
            {
                var cookies = new CookieContainer();
                var handler = new HttpClientHandler
                {
                    CookieContainer         = cookies,
                    AllowAutoRedirect       = true,
                    MaxAutomaticRedirections = 10,
                    UseCookies              = true,
                };
                var http = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };
                http.DefaultRequestHeaders.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                    "AppleWebKit/537.36 (KHTML, like Gecko) " +
                    "Chrome/124.0 Safari/537.36");
                http.DefaultRequestHeaders.Add("Accept",
                    "text/html,application/xhtml+xml,application/json,*/*");

                return new CanvasService(
                    http,
                    sp.GetRequiredService<ILoggingService>(),
                    sp.GetRequiredService<ISecureConfigService>());
            });

            builder.Services.AddSingleton<IInfiniteCampusService>(sp =>
            {
                var cookies = new CookieContainer();
                var handler = new HttpClientHandler
                {
                    CookieContainer         = cookies,
                    AllowAutoRedirect       = true,
                    MaxAutomaticRedirections = 10,
                    UseCookies              = true,
                };
                var http = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };
                http.DefaultRequestHeaders.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                    "AppleWebKit/537.36 (KHTML, like Gecko) " +
                    "Chrome/124.0 Safari/537.36");

                return new InfiniteCampusService(
                    http,
                    sp.GetRequiredService<ILoggingService>(),
                    sp.GetRequiredService<ISecureConfigService>());
            });

            builder.Services.AddSingleton<IGmailService, GmailService>();

            // OpenAI uses a plain HttpClient (no cookies needed, API key in header).
            builder.Services.AddSingleton<IOpenAIService>(sp =>
            {
                var http = new HttpClient
                {
                    BaseAddress = new Uri("https://api.openai.com/"),
                    Timeout     = TimeSpan.FromSeconds(90)
                };
                http.DefaultRequestHeaders.Add("Accept", "application/json");
                return new OpenAIService(
                    http,
                    sp.GetRequiredService<ILoggingService>(),
                    sp.GetRequiredService<ISecureConfigService>());
            });

            // Generic client for Data Dump URL fetching.
            builder.Services.AddSingleton<System.Net.Http.HttpClient>(sp =>
                new HttpClient { Timeout = TimeSpan.FromSeconds(15) });

            // ---- ViewModels ----

            builder.Services.AddTransient<DashboardViewModel>();
            builder.Services.AddTransient<GmailViewModel>();
            builder.Services.AddTransient<AssignmentsViewModel>();
            builder.Services.AddTransient<DataDumpViewModel>(sp =>
                new DataDumpViewModel(
                    sp.GetRequiredService<ICanvasService>(),
                    sp.GetRequiredService<IOpenAIService>(),
                    sp.GetRequiredService<System.Net.Http.HttpClient>(),
                    sp.GetRequiredService<ILoggingService>()));
            builder.Services.AddTransient<SettingsViewModel>();

            // BUG FIX: ReviewViewModel MUST be a singleton.
            // Previously it was AddTransient, which meant AssignmentsPage,
            // DataDumpPage, and ReviewPage each received a *different* instance.
            // Items added by Assignments/DataDump were added to an orphaned VM
            // that ReviewPage never saw, so the Review tab was always empty.
            builder.Services.AddSingleton<ReviewViewModel>();

            // ---- Pages ----
            builder.Services.AddTransient<DashboardPage>();
            builder.Services.AddTransient<GmailPage>();
            builder.Services.AddTransient<AssignmentsPage>();
            builder.Services.AddTransient<DataDumpPage>();
            builder.Services.AddTransient<ReviewPage>();
            builder.Services.AddTransient<SettingsPage>();

            return builder.Build();
        }
    }
}