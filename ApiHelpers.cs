// Helpers/ApiHelpers.cs
// Contains: ApiRateLimitHandler, TokenRefreshHandler, and all custom exceptions.

using System.Net;
using EduAutomation.Services;
// BUG FIX: Added missing Polly using directives. Without these the file fails to
// compile because IAsyncPolicy<T>, HttpPolicyExtensions, and Policy are all
// defined in the Polly / Polly.Extensions.Http packages.
using Polly;
using Polly.Extensions.Http;

namespace EduAutomation.Helpers
{
    // ============================================================
    // ApiRateLimitHandler
    // ============================================================

    public static class ApiRateLimitHandler
    {
        public static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy(
            ILoggingService? log = null,
            int retryCount = 3)
        {
            return HttpPolicyExtensions
                .HandleTransientHttpError()
                .OrResult(msg => msg.StatusCode == (HttpStatusCode)429)
                .WaitAndRetryAsync(
                    retryCount: retryCount,
                    sleepDurationProvider: (retryAttempt, response, context) =>
                    {
                        if (response?.Result?.Headers?.RetryAfter?.Delta != null)
                            return response.Result.Headers.RetryAfter.Delta.Value;
                        return TimeSpan.FromSeconds(Math.Pow(2, retryAttempt));
                    },
                    onRetryAsync: (response, delay, attempt, context) =>
                    {
                        string reason = response.Exception?.Message
                            ?? response.Result?.StatusCode.ToString()
                            ?? "Unknown";
                        log?.LogApiRetry(
                            source: context.OperationKey ?? "HttpClient",
                            attempt: attempt,
                            maxAttempts: retryCount,
                            delayMs: (int)delay.TotalMilliseconds,
                            reason: reason);
                        return Task.CompletedTask;
                    });
        }

        public static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy(
            ILoggingService? log = null)
        {
            return HttpPolicyExtensions
                .HandleTransientHttpError()
                .CircuitBreakerAsync(
                    handledEventsAllowedBeforeBreaking: 5,
                    durationOfBreak: TimeSpan.FromSeconds(30),
                    onBreak: (response, breakDuration) =>
                    {
                        log?.LogWarning("CircuitBreaker",
                            $"Circuit OPENED for {breakDuration.TotalSeconds}s. " +
                            $"Last status: {response.Result?.StatusCode}");
                    },
                    onReset:    () => { log?.LogInfo("CircuitBreaker", "Circuit RESET."); },
                    onHalfOpen: () => { log?.LogInfo("CircuitBreaker", "Circuit HALF-OPEN."); });
        }

        public static IAsyncPolicy<HttpResponseMessage> GetCombinedPolicy(
            ILoggingService? log = null)
        {
            return Policy.WrapAsync(GetRetryPolicy(log), GetCircuitBreakerPolicy(log));
        }
    }

    // ============================================================
    // TokenRefreshHandler
    // ============================================================

    public class TokenRefreshHandler : DelegatingHandler
    {
        private readonly ILoggingService _log;
        private readonly ISecureConfigService _config;
        private readonly Func<Task>? _onTokenExpired;

        public TokenRefreshHandler(
            ILoggingService log,
            ISecureConfigService config,
            Func<Task>? onTokenExpired = null)
        {
            _log            = log;
            _config         = config;
            _onTokenExpired = onTokenExpired;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _log.LogWarning("TokenRefreshHandler.SendAsync",
                    $"HTTP 401 received for {request.RequestUri}. Token may have expired.");
                if (_onTokenExpired != null)
                    await _onTokenExpired.Invoke();
            }
            return response;
        }
    }
}

namespace EduAutomation.Exceptions
{
    // ============================================================
    // Custom Exception Types
    // ============================================================

    public class UnauthorizedSubmissionException : InvalidOperationException
    {
        public string AssignmentId  { get; }
        public string ReviewItemId  { get; }

        public UnauthorizedSubmissionException(string assignmentId, string reviewItemId)
            : base($"Assignment '{assignmentId}' cannot be submitted: ReviewItem '{reviewItemId}' " +
                   "has not been explicitly approved by the user. " +
                   "Set IsApprovedByUser = true on the ReviewItem to proceed.")
        {
            AssignmentId = assignmentId;
            ReviewItemId = reviewItemId;
        }
    }

    public class ServiceUnavailableException : Exception
    {
        public string ServiceName { get; }
        public ServiceUnavailableException(string serviceName, string message, Exception? innerException = null)
            : base(message, innerException)
        {
            ServiceName = serviceName;
        }
    }

    public class AiHallucinationGuardException : Exception
    {
        public AiHallucinationGuardException(string message) : base(message) { }
    }
}