// Services/IOpenAIService.cs + OpenAIService.cs
// Integrates with the OpenAI Chat Completions API using GPT-4o.
// Applies PromptGuardrails before sending and after receiving to prevent
// hallucination and ensure 9th-grade appropriate output.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

    // ---- Request/Response DTOs ----

    internal class OpenAiMessage
    {
        [JsonProperty("role")] public string Role { get; set; } = string.Empty;
        [JsonProperty("content")] public string Content { get; set; } = string.Empty;
    }

    internal class OpenAiRequest
    {
        [JsonProperty("model")] public string Model { get; set; } = string.Empty;
        [JsonProperty("messages")] public List<OpenAiMessage> Messages { get; set; } = new();
        [JsonProperty("max_tokens")] public int MaxTokens { get; set; } = 2048;
        [JsonProperty("temperature")] public double Temperature { get; set; } = 0.7;
    }

    // ---- Implementation ----

    public class OpenAIService : IOpenAIService
    {
        private const string Source = "OpenAIService";
        private const string ModelId = "gpt-4o";
        private const string ApiUrl = "https://api.openai.com/v1/chat/completions";
        private const int MaxTokens = 2048;

        private readonly HttpClient _httpClient;
        private readonly ILoggingService _log;
        private readonly ISecureConfigService _config;

        public OpenAIService(
            HttpClient httpClient,
            ILoggingService log,
            ISecureConfigService config)
        {
            _httpClient = httpClient;
            _log = log;
            _config = config;
        }

        public async Task<bool> ValidateApiKeyAsync(CancellationToken ct = default)
        {
            _log.LogInfo(Source, "Validating OpenAI API key...");
            try
            {
                await EnsureAuthHeaderAsync();
                var testRequest = new OpenAiRequest
                {
                    Model = ModelId,
                    Messages = new List<OpenAiMessage>
                    {
                        new OpenAiMessage { Role = "user", Content = "Say: OK" }
                    },
                    MaxTokens = 5
                };

                string json = JsonConvert.SerializeObject(testRequest);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var sw = Stopwatch.StartNew();
                HttpResponseMessage response = await _httpClient.PostAsync(ApiUrl, content, ct);
                sw.Stop();
                _log.LogApiCall(Source, "POST", ApiUrl, (int)response.StatusCode, sw.ElapsedMilliseconds);

                if (response.IsSuccessStatusCode)
                {
                    _log.LogInfo(Source, "OpenAI API key is valid.");
                    return true;
                }
                _log.LogWarning(Source, $"OpenAI validation failed: HTTP {(int)response.StatusCode}");
                return false;
            }
            catch (Exception ex)
            {
                _log.LogError(Source, "Exception during OpenAI key validation.", ex);
                return false;
            }
        }

        public async Task<ReviewItem> GenerateAssignmentResponseAsync(
            Assignment assignment,
            List<string> supplementalData,
            CancellationToken ct = default)
        {
            _log.LogInfo(Source,
                $"Generating AI response for assignment: '{assignment.Title}' " +
                $"(Course: {assignment.CourseName})");

            // Step 1: Validate the assignment data before building the prompt.
            // This throws AiHallucinationGuardException if data is insufficient.
            PromptGuardrails.ValidateAssignmentForPrompt(assignment);

            // Step 2: Build the system and user prompts with guardrails applied.
            var (systemPrompt, userPrompt) = PromptGuardrails.BuildAssignmentPrompt(
                assignment, supplementalData);

            _log.LogDebug(Source,
                $"Prompt built. System prompt: {systemPrompt.Length} chars. " +
                $"User prompt: {userPrompt.Length} chars.");

            // Step 3: Call the OpenAI API.
            var sw = Stopwatch.StartNew();
            string rawResponse;
            int totalTokens = 0;

            try
            {
                await EnsureAuthHeaderAsync();

                var request = new OpenAiRequest
                {
                    Model = ModelId,
                    Messages = new List<OpenAiMessage>
                    {
                        new OpenAiMessage { Role = "system", Content = systemPrompt },
                        new OpenAiMessage { Role = "user", Content = userPrompt }
                    },
                    MaxTokens = MaxTokens,
                    Temperature = 0.7
                };

                string requestJson = JsonConvert.SerializeObject(request);
                var httpContent = new StringContent(requestJson, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await _httpClient.PostAsync(ApiUrl, httpContent, ct);
                sw.Stop();
                _log.LogApiCall(Source, "POST", ApiUrl, (int)response.StatusCode, sw.ElapsedMilliseconds);

                if (!response.IsSuccessStatusCode)
                {
                    string errorBody = await response.Content.ReadAsStringAsync(ct);
                    _log.LogError(Source,
                        $"OpenAI API error HTTP {(int)response.StatusCode}: {errorBody}");
                    throw new ServiceUnavailableException("OpenAI",
                        $"OpenAI API returned error {(int)response.StatusCode}. " +
                        "Check your API key and account quota.");
                }

                string responseJson = await response.Content.ReadAsStringAsync(ct);
                var parsed = JObject.Parse(responseJson);

                rawResponse = parsed["choices"]?[0]?["message"]?["content"]?.ToString()
                    ?? string.Empty;
                totalTokens = parsed["usage"]?["total_tokens"]?.Value<int>() ?? 0;

                _log.LogInfo(Source,
                    $"OpenAI response received. Tokens used: {totalTokens}. " +
                    $"Response length: {rawResponse.Length} chars.");
            }
            catch (ServiceUnavailableException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _log.LogError(Source,
                    $"Exception calling OpenAI for assignment '{assignment.Title}'.", ex);
                throw new ServiceUnavailableException("OpenAI",
                    "Failed to contact the OpenAI API. Check internet connectivity.", ex);
            }

            // Step 4: Validate the response for hallucinations and refusals.
            // This throws AiHallucinationGuardException if the response is invalid.
            PromptGuardrails.ValidateAiResponse(rawResponse, assignment.Title);

            // Step 5: Build and return the ReviewItem.
            // IsApprovedByUser is always false on creation -- the user must approve explicitly.
            var reviewItem = new ReviewItem
            {
                SourceAssignment = assignment,
                OriginalAiContent = rawResponse,
                EditedContent = rawResponse,
                IsApprovedByUser = false,
                Status = ReviewItemStatus.PendingReview,
                PromptSentToAi = $"SYSTEM:\n{systemPrompt}\n\nUSER:\n{userPrompt}",
                AiModelUsed = ModelId,
                TotalTokensUsed = totalTokens,
                GeneratedAt = DateTimeOffset.UtcNow
            };

            _log.LogInfo(Source,
                $"ReviewItem {reviewItem.ReviewId} created for '{assignment.Title}'. " +
                "Status: PendingReview. Awaiting user approval.");

            return reviewItem;
        }

        private async Task EnsureAuthHeaderAsync()
        {
            string? apiKey = await _config.GetOpenAiApiKeyAsync();
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ServiceUnavailableException("OpenAI",
                    "OpenAI API key is not configured. Please set it in Settings.");

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }
}
