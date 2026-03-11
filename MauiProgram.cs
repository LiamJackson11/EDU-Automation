// MauiProgram.cs
// Application entry point. Configures the .NET MAUI app builder,
// registers all services with the dependency injection container,
// and sets up HttpClient policies via IHttpClientFactory.

using System;
using System.Net.Http;
using EduAutomation.Helpers;
using EduAutomation.Services;
using EduAutomation.ViewModels;
using EduAutomation.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;
using Polly;
using Serilog;
using Serilog.Extensions.Logging;

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
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            // ---- Configure Serilog as the logging provider ----
            var serilogLogger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Debug()
                .CreateLogger();

            builder.Logging
                .ClearProviders()
                .AddSerilog(serilogLogger);

            // ---- Register Core Services as Singletons ----
            builder.Services.AddSingleton<ILoggingService, LoggingService>();
            builder.Services.AddSingleton<ISecureConfigService, SecureConfigService>();

            // ---- Register Named HttpClients with Polly resilience policies ----
            // Each external API gets its own named HttpClient with retry + circuit breaker.

            // Canvas HttpClient
            builder.Services.AddHttpClient("Canvas", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            })
            .AddPolicyHandler((services, request) =>
            {
                var log = services.GetRequiredService<ILoggingService>();
                return ApiRateLimitHandler.GetCombinedPolicy(log);
            });

            // OpenAI HttpClient
            builder.Services.AddHttpClient("OpenAI", client =>
            {
                client.BaseAddress = new Uri("https://api.openai.com/");
                client.Timeout = TimeSpan.FromSeconds(90);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            })
            .AddPolicyHandler((services, request) =>
            {
                var log = services.GetRequiredService<ILoggingService>();
                return ApiRateLimitHandler.GetCombinedPolicy(log);
            });

            // Infinite Campus HttpClient (with cookie handling for scraping)
            builder.Services.AddHttpClient("InfiniteCampus", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("User-Agent",
                    "Mozilla/5.0 (compatible; EduAutomation/1.0)");
            });

            // Generic HttpClient for Data Dump URL fetching
            builder.Services.AddHttpClient("DataDump", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
            });

            // ---- Register API Services ----
            builder.Services.AddSingleton<IGmailService, GmailService>();

            builder.Services.AddSingleton<ICanvasService>(sp =>
            {
                var factory = sp.GetRequiredService<IHttpClientFactory>();
                return new CanvasService(
                    factory.CreateClient("Canvas"),
                    sp.GetRequiredService<ILoggingService>(),
                    sp.GetRequiredService<ISecureConfigService>());
            });

            builder.Services.AddSingleton<IInfiniteCampusService>(sp =>
            {
                var factory = sp.GetRequiredService<IHttpClientFactory>();
                return new InfiniteCampusService(
                    factory.CreateClient("InfiniteCampus"),
                    sp.GetRequiredService<ILoggingService>(),
                    sp.GetRequiredService<ISecureConfigService>());
            });

            builder.Services.AddSingleton<IOpenAIService>(sp =>
            {
                var factory = sp.GetRequiredService<IHttpClientFactory>();
                return new OpenAIService(
                    factory.CreateClient("OpenAI"),
                    sp.GetRequiredService<ILoggingService>(),
                    sp.GetRequiredService<ISecureConfigService>());
            });

            // ---- Register ViewModels as Transients ----
            builder.Services.AddTransient<DashboardViewModel>();
            builder.Services.AddTransient<GmailViewModel>();
            builder.Services.AddTransient<AssignmentsViewModel>();
            builder.Services.AddTransient<DataDumpViewModel>(sp =>
            {
                var factory = sp.GetRequiredService<IHttpClientFactory>();
                return new DataDumpViewModel(
                    sp.GetRequiredService<ICanvasService>(),
                    sp.GetRequiredService<IOpenAIService>(),
                    factory.CreateClient("DataDump"),
                    sp.GetRequiredService<ILoggingService>());
            });
            builder.Services.AddTransient<ReviewViewModel>();
            builder.Services.AddTransient<SettingsViewModel>();

            // ---- Register Pages ----
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
