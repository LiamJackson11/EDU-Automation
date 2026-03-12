// Services/ILoggingService.cs + LoggingService.cs
// Wraps Serilog with timestamped, structured logging.
// Writes to the Debug output window AND a rolling daily file on disk.
// Built-in sanitizer prevents API keys and passwords from appearing in logs.

using System.Text.RegularExpressions;
using Serilog;
using Serilog.Events;

namespace EduAutomation.Services
{
    // ---- Interface ----

    public interface ILoggingService
    {
        void LogInfo   (string source, string message);
        void LogWarning(string source, string message);
        void LogError  (string source, string message, Exception? ex = null);
        void LogDebug  (string source, string message);
        void LogApiCall(string source, string httpMethod, string url,
                        int statusCode, long elapsedMs);
        void LogApiRetry(string source, int attempt, int maxAttempts,
                         int delayMs, string reason);
        void LogRateLimit(string source, string service, int retryAfterSeconds);
        void LogSessionExpired(string source, string service);
        void LogSubmissionBlocked(string source, string assignmentId, string reason);
        void LogGuardrailTriggered(string source, string indicator, string assignmentTitle);

        /// Returns the path of the log directory (shown in Settings for user support).
        string LogFilePath { get; }
    }

    // ---- Implementation ----

    public class LoggingService : ILoggingService
    {
        private readonly ILogger _logger;
        private readonly string  _logDir;

        // BUG FIX: LogFilePath previously returned "log-.txt" — the Serilog
        // filename template, not an actual file. Serilog names rolling files
        // "log-20260311.txt" etc. Pointing users at the template path sent them
        // looking for a file that doesn't exist.
        // Fix: expose the log *directory* so the user (or a support page) can
        // browse to the folder and find today's file. Alternatively compute the
        // current file name dynamically (shown as LogFilePathToday below).
        public string LogFilePath => _logDir;

        /// The current day's log file path, useful for direct file sharing.
        public string LogFilePathToday =>
            Path.Combine(_logDir, $"log-{DateTime.Now:yyyyMMdd}.txt");

        // Matches common API key / token formats and replaces them with [REDACTED].
        private static readonly Regex SanitizePattern = new(
            @"(sk-[A-Za-z0-9\-_]{20,}" +
            @"|Bearer\s+[A-Za-z0-9\-_\.]{20,}" +
            @"|token[=:]\s*[A-Za-z0-9\-_\.]{10,}" +
            @"|password[=:]\s*\S+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const string OutputTemplate =
            "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}";

        public LoggingService()
        {
            _logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EduAutomation", "logs");
            Directory.CreateDirectory(_logDir);

            string logFileTemplate = Path.Combine(_logDir, "log-.txt");

            _logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Debug(outputTemplate: OutputTemplate)
                .WriteTo.File(
                    path:                   logFileTemplate,
                    rollingInterval:        RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate:         OutputTemplate)
                .CreateLogger();

            _logger.Information("[LoggingService] Logging initialized. Directory: {LogDir}", _logDir);
        }

        public void LogInfo(string source, string message) =>
            _logger.Information("[{Source}] {Message}", source, Sanitize(message));

        public void LogWarning(string source, string message) =>
            _logger.Warning("[{Source}] {Message}", source, Sanitize(message));

        public void LogError(string source, string message, Exception? ex = null)
        {
            if (ex != null)
                _logger.Error(ex, "[{Source}] {Message}", source, Sanitize(message));
            else
                _logger.Error("[{Source}] {Message}", source, Sanitize(message));
        }

        public void LogDebug(string source, string message) =>
            _logger.Debug("[{Source}] {Message}", source, Sanitize(message));

        public void LogApiCall(string source, string httpMethod, string url,
            int statusCode, long elapsedMs)
        {
            var level = statusCode >= 500 ? LogEventLevel.Error
                      : statusCode >= 400 ? LogEventLevel.Warning
                      : LogEventLevel.Information;

            _logger.Write(level,
                "[{Source}] API {Method} {Url} -> HTTP {StatusCode} ({Elapsed}ms)",
                source, httpMethod, Sanitize(url), statusCode, elapsedMs);
        }

        public void LogApiRetry(string source, int attempt, int maxAttempts,
            int delayMs, string reason)
        {
            _logger.Warning(
                "[{Source}] RETRY {Attempt}/{Max} after {Delay}ms. Reason: {Reason}",
                source, attempt, maxAttempts, delayMs, Sanitize(reason));
        }

        public void LogRateLimit(string source, string service, int retryAfterSeconds)
        {
            _logger.Warning(
                "[{Source}] RATE LIMITED by {Service}. " +
                "Retry-After header says: {RetryAfterSeconds}s. " +
                "Backing off before next attempt.",
                source, service, retryAfterSeconds);
        }

        public void LogSessionExpired(string source, string service)
        {
            _logger.Warning(
                "[{Source}] SESSION EXPIRED for {Service}. " +
                "Triggering automatic re-authentication.",
                source, service);
        }

        public void LogSubmissionBlocked(string source, string assignmentId, string reason)
        {
            _logger.Error(
                "[{Source}] SUBMISSION BLOCKED for assignment '{AssignmentId}'. Reason: {Reason}",
                source, assignmentId, Sanitize(reason));
        }

        public void LogGuardrailTriggered(string source, string indicator, string assignmentTitle)
        {
            _logger.Warning(
                "[{Source}] AI GUARDRAIL TRIGGERED. " +
                "Indicator='{Indicator}' in response for assignment '{Title}'. " +
                "Response rejected.",
                source, indicator, assignmentTitle);
        }

        private static string Sanitize(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            return SanitizePattern.Replace(input, "[REDACTED]");
        }
    }
}