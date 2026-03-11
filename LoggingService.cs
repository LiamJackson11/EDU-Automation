// Services/ILoggingService.cs + LoggingService.cs
// Wraps Serilog with timestamped, structured logging to both the Debug output
// window and a rolling file on disk. Includes a built-in sanitizer to prevent
// API keys or passwords from appearing in log output.

using System;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Events;

namespace EduAutomation.Services
{
    // ---- Interface ----

    public interface ILoggingService
    {
        void LogInfo(string source, string message);
        void LogWarning(string source, string message);
        void LogError(string source, string message, Exception? ex = null);
        void LogDebug(string source, string message);
        void LogApiCall(string source, string httpMethod, string url, int statusCode, long elapsedMs);
        void LogApiRetry(string source, int attempt, int maxAttempts, int delayMs, string reason);
    }

    // ---- Implementation ----

    public class LoggingService : ILoggingService
    {
        private readonly ILogger _logger;

        // Regex pattern that matches common API key formats for sanitization.
        private static readonly Regex ApiKeyPattern =
            new Regex(@"(sk-[A-Za-z0-9\-_]{20,}|Bearer\s+[A-Za-z0-9\-_\.]{20,}|token[=:]\s*[A-Za-z0-9\-_\.]{10,})",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public LoggingService()
        {
            string logDirectory = GetLogDirectory();
            string logFilePath = System.IO.Path.Combine(logDirectory, "log-.txt");

            _logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Debug(
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(
                    path: logFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            _logger.Information("[LoggingService.ctor] Logging initialized. Log path: {LogPath}", logFilePath);
        }

        public void LogInfo(string source, string message)
        {
            _logger.Information("[{Source}] {Message}", source, Sanitize(message));
        }

        public void LogWarning(string source, string message)
        {
            _logger.Warning("[{Source}] {Message}", source, Sanitize(message));
        }

        public void LogError(string source, string message, Exception? ex = null)
        {
            if (ex != null)
                _logger.Error(ex, "[{Source}] {Message}", source, Sanitize(message));
            else
                _logger.Error("[{Source}] {Message}", source, Sanitize(message));
        }

        public void LogDebug(string source, string message)
        {
            _logger.Debug("[{Source}] {Message}", source, Sanitize(message));
        }

        public void LogApiCall(string source, string httpMethod, string url, int statusCode, long elapsedMs)
        {
            var level = statusCode >= 400 ? LogEventLevel.Warning : LogEventLevel.Information;
            _logger.Write(level,
                "[{Source}] API {Method} {Url} -> HTTP {StatusCode} in {ElapsedMs}ms",
                source, httpMethod, Sanitize(url), statusCode, elapsedMs);
        }

        public void LogApiRetry(string source, int attempt, int maxAttempts, int delayMs, string reason)
        {
            _logger.Warning(
                "[{Source}] Retry attempt {Attempt}/{MaxAttempts} after {DelayMs}ms. Reason: {Reason}",
                source, attempt, maxAttempts, delayMs, reason);
        }

        // Replaces potential API keys or tokens in log strings with [REDACTED].
        private static string Sanitize(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return ApiKeyPattern.Replace(input, "[REDACTED]");
        }

        private static string GetLogDirectory()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string logDir = System.IO.Path.Combine(baseDir, "EduAutomation", "logs");
            System.IO.Directory.CreateDirectory(logDir);
            return logDir;
        }
    }
}
