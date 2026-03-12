// Services/IOpenAIService.cs + OpenAIService.cs
// GPT-4o integration with full rate-limit handling, retry logic,
// and strict hallucination guardrails via PromptGuardrails.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EduAutomation.Exceptions;
using EduAutomation.Helpers;
using EduAutomation.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EduAutomation.Services
{
    // ---- Interface ----

    public interface IOpenAIService
    {
        Task<ReviewItem> GenerateAssignmentResponseAsync(
            Assignment assignment,
            List<string> supplementalData,
            CancellationToken ct = default);

        Task<bool> ValidateApiKeyAsync(CancellationToken ct = default);
    }

    // ---- DTOs ----

    internal class OpenAiMessage
    {
        [JsonProperty("role")]    public string Role    { get; set; } = string.Empty;
        [JsonProperty("content")] public string Content { get; set; } = string.Empty;
    }

    internal class OpenAiRequest
    {
        [JsonProperty("model")]       public string              Model       { get; set; } = string.Empty;
        [JsonProperty("messages")]    public List<OpenAiMessage> Messages    { get; set; } = new();
        [JsonProperty("max_tokens")]  public int                 MaxTokens   { get; set; } = 2048;
        [JsonProperty("temperature")] public double              Temperature { get; set; } = 0.7;
    }

    // ---- Implementation ----

    public class OpenAIService : IOpenAIService
    {
        private const string Source          = "OpenAIService";
        private const string ModelId         = "gpt-4o";
        private const string ApiUrl          = "https://api.openai.com/v1/chat/completions";
        private const int    MaxTokens       = 2048;
        private const int    MaxRetries      = 3;
        private const int    BaseRetryDelayMs = 1000;
        private const int    MaxRetryDelayMs  = 60_000;

        private readonly HttpClient           _http;
        private readonly ILoggingService      _log;
        private readonly ISecureConfigService _config;

        public OpenAIService(
            HttpClient http, ILoggingService log, ISecureConfigService config)
        {
            _http   = http;
            _log    = log;
            _config = config;
        }

        // ---- Validate API key ----

        public async Task<bool> ValidateApiKeyAsync(CancellationToken ct = default)
        {
            _log.LogInfo(Source, "Validating OpenAI API key with a minimal test call...");
            var sw = Stopwatch.StartNew();
            try
            {
                string? apiKey = await _config.GetOpenAiApiKeyAsync();
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    _log.LogWarning(Source, "OpenAI API key is not configured.");
                    return false;
                }

                var req = new OpenAiRequest
                {
                    Model     = ModelId,
                    Messages  = new() { new OpenAiMessage { Role = "user", Content = "Say: OK" } },
                    MaxTokens = 5
                };

                // BUG FIX: Build a per-request HttpRequestMessage with its own
                // Authorization header rather than setting DefaultRequestHeaders.
                // HttpClient is registered as a singleton and shared across threads;
                // mutating DefaultRequestHeaders on every call is a race condition.
                using var request = BuildRequest(
                    HttpMethod.Post, ApiUrl,
                    new StringContent(JsonConvert.SerializeObject(req), Encoding.UTF8, "application/json"),
                    apiKey);

                HttpResponseMessage resp = await _http.SendAsync(request, ct);
                sw.Stop();
                _log.LogApiCall(Source, "POST", ApiUrl, (int)resp.StatusCode, sw.ElapsedMilliseconds);

                if (resp.IsSuccessStatusCode)
                {
                    _log.LogInfo(Source, "OpenAI API key validated successfully.");
                    return true;
                }

                string body = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning(Source,
                    $"OpenAI key validation failed HTTP {(int)resp.StatusCode}. Body: {body}");
                return false;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _log.LogError(Source, "Exception during OpenAI key validation.", ex);
                return false;
            }
        }

        // ---- Generate assignment response ----

        public async Task<ReviewItem> GenerateAssignmentResponseAsync(
            Assignment assignment,
            List<string> supplementalData,
            CancellationToken ct = default)
        {
            _log.LogInfo(Source,
                $"Starting AI generation for assignment: '{assignment.Title}' " +
                $"(Course: {assignment.CourseName}, Source: {assignment.Source})");

            // GUARDRAIL STEP 1: Validate the assignment data before building any prompt.
            PromptGuardrails.ValidateAssignmentForPrompt(assignment);

            // GUARDRAIL STEP 2: Build the graded/restricted prompts.
            var (systemPrompt, userPrompt) = PromptGuardrails.BuildAssignmentPrompt(
                assignment, supplementalData);

            _log.LogDebug(Source,
                $"Prompt built. System: {systemPrompt.Length} chars. " +
                $"User: {userPrompt.Length} chars. " +
                $"Supplemental items: {supplementalData.Count}.");

            // STEP 3: Call the API with retry + rate-limit handling.
            string rawResponse = await CallOpenAiWithRetryAsync(
                systemPrompt, userPrompt, ct);

            _log.LogDebug(Source,
                $"Raw OpenAI response received. Length: {rawResponse.Length} chars.");

            // GUARDRAIL STEP 4: Validate the response.
            PromptGuardrails.ValidateAiResponse(rawResponse, assignment.Title);

            // STEP 5: Build the ReviewItem. IsApprovedByUser is always false on creation.
            var reviewItem = new ReviewItem
            {
                SourceAssignment  = assignment,
                OriginalAiContent = rawResponse,
                EditedContent     = rawResponse,
                IsApprovedByUser  = false,                // USER MUST APPROVE EXPLICITLY
                Status            = ReviewItemStatus.PendingReview,
                PromptSentToAi    = $"SYSTEM:\n{systemPrompt}\n\nUSER:\n{userPrompt}",
                AiModelUsed       = ModelId,
                GeneratedAt       = DateTimeOffset.UtcNow
            };

            _log.LogInfo(Source,
                $"ReviewItem '{reviewItem.ReviewId}' created for '{assignment.Title}'. " +
                $"Status=PendingReview. IsApprovedByUser=FALSE. Awaiting explicit user approval.");

            return reviewItem;
        }

        // ---- Internal: call API with exponential backoff + 429 handling ----

        private async Task<string> CallOpenAiWithRetryAsync(
            string systemPrompt, string userPrompt, CancellationToken ct)
        {
            string? apiKey = await _config.GetOpenAiApiKeyAsync();
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ServiceUnavailableException("OpenAI",
                    "OpenAI API key is not configured. Please add it in Settings.");

            var requestBody = new OpenAiRequest
            {
                Model    = ModelId,
                Messages = new()
                {
                    new OpenAiMessage { Role = "system", Content = systemPrompt },
                    new OpenAiMessage { Role = "user",   Content = userPrompt }
                },
                MaxTokens   = MaxTokens,
                Temperature = 0.7
            };

            string requestJson = JsonConvert.SerializeObject(requestBody);

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                var sw = Stopwatch.StartNew();
                HttpResponseMessage? resp = null;
                try
                {
                    // BUG FIX: Create a fresh HttpRequestMessage per attempt so the
                    // Authorization header is scoped to this request only. The old code
                    // called SetAuthHeaderAsync which mutated DefaultRequestHeaders on
                    // the shared singleton HttpClient — a race condition if two requests
                    // run concurrently (e.g. validate key + generate content at once).
                    using var request = BuildRequest(
                        HttpMethod.Post, ApiUrl,
                        new StringContent(requestJson, Encoding.UTF8, "application/json"),
                        apiKey);

                    resp = await _http.SendAsync(request, ct);
                    sw.Stop();
                    _log.LogApiCall(Source, "POST", ApiUrl, (int)resp.StatusCode, sw.ElapsedMilliseconds);

                    // ---- Rate limit (429) ----
                    if (resp.StatusCode == (HttpStatusCode)429)
                    {
                        int retryAfter = GetRetryAfterSeconds(resp, attempt);
                        _log.LogRateLimit(Source, "OpenAI", retryAfter);

                        if (attempt < MaxRetries)
                        {
                            _log.LogApiRetry(Source, attempt, MaxRetries,
                                retryAfter * 1000, "HTTP 429 Rate Limit");
                            await Task.Delay(retryAfter * 1000, ct);
                            continue;
                        }

                        throw new ServiceUnavailableException("OpenAI",
                            $"OpenAI rate limit exceeded after {MaxRetries} attempts. " +
                            "Wait a few minutes and try again.");
                    }

                    // ---- Auth failure ----
                    if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        string errBody = await resp.Content.ReadAsStringAsync(ct);
                        _log.LogError(Source,
                            $"OpenAI returned HTTP 401. API key may be invalid or expired. " +
                            $"Response: {errBody}");
                        throw new ServiceUnavailableException("OpenAI",
                            "OpenAI authentication failed (HTTP 401). " +
                            "Check your API key in Settings.");
                    }

                    // ---- Other HTTP errors ----
                    if (!resp.IsSuccessStatusCode)
                    {
                        string errBody = await resp.Content.ReadAsStringAsync(ct);
                        _log.LogError(Source,
                            $"OpenAI HTTP {(int)resp.StatusCode}. Body: {errBody}");

                        // Retry on 5xx server errors.
                        if ((int)resp.StatusCode >= 500 && attempt < MaxRetries)
                        {
                            int delayMs = Math.Min(BaseRetryDelayMs * (int)Math.Pow(2, attempt),
                                MaxRetryDelayMs);
                            _log.LogApiRetry(Source, attempt, MaxRetries, delayMs,
                                $"HTTP {(int)resp.StatusCode} server error");
                            await Task.Delay(delayMs, ct);
                            continue;
                        }

                        throw new ServiceUnavailableException("OpenAI",
                            $"OpenAI API error HTTP {(int)resp.StatusCode}. " +
                            "Check your account quota and API key.");
                    }

                    // ---- Success: parse response ----
                    string responseJson = await resp.Content.ReadAsStringAsync(ct);
                    JObject parsed      = JObject.Parse(responseJson);

                    string content = parsed["choices"]?[0]?["message"]?["content"]?.ToString()
                        ?? string.Empty;
                    int tokens = parsed["usage"]?["total_tokens"]?.Value<int>() ?? 0;

                    _log.LogInfo(Source,
                        $"OpenAI success on attempt {attempt}/{MaxRetries}. " +
                        $"Tokens used: {tokens}. Response: {content.Length} chars.");

                    return content;
                }
                catch (ServiceUnavailableException) { throw; }
                catch (OperationCanceledException)  { throw; }
                catch (Exception ex)
                {
                    sw.Stop();
                    _log.LogError(Source,
                        $"Exception calling OpenAI (attempt {attempt}/{MaxRetries}).", ex);

                    if (attempt >= MaxRetries)
                        throw new ServiceUnavailableException("OpenAI",
                            "Failed to contact the OpenAI API after multiple attempts. " +
                            "Check your internet connection.", ex);

                    int delayMs = Math.Min(BaseRetryDelayMs * (int)Math.Pow(2, attempt),
                        MaxRetryDelayMs);
                    await Task.Delay(delayMs, ct);
                }
            }

            throw new ServiceUnavailableException("OpenAI",
                "OpenAI API unreachable after all retry attempts.");
        }

        // ---- Helpers ----

        /// Creates an HttpRequestMessage with a per-request Authorization header.
        private static HttpRequestMessage BuildRequest(
            HttpMethod method, string url, HttpContent content, string apiKey)
        {
            var msg = new HttpRequestMessage(method, url) { Content = content };
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            return msg;
        }

        private static int GetRetryAfterSeconds(HttpResponseMessage resp, int attempt)
        {
            if (resp.Headers.RetryAfter?.Delta != null)
                return (int)resp.Headers.RetryAfter.Delta.Value.TotalSeconds;
            if (resp.Headers.RetryAfter?.Date != null)
            {
                int remaining = (int)(resp.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow)
                    .TotalSeconds;
                return Math.Max(remaining, 1);
            }
            // Exponential backoff fallback: 2s, 4s, 8s
            return Math.Min(2 * (int)Math.Pow(2, attempt - 1), 60);
        }
    }
}