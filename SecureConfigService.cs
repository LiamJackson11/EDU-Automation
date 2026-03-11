// Services/ISecureConfigService.cs + SecureConfigService.cs
// Wraps .NET MAUI's SecureStorage for all API credential storage and retrieval.
// Falls back to environment variables during development.

using System;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

namespace EduAutomation.Services
{
    // ---- Interface ----

    public interface ISecureConfigService
    {
        Task<string?> GetCanvasBaseUrlAsync();
        Task<string?> GetCanvasApiTokenAsync();
        Task<string?> GetOpenAiApiKeyAsync();
        Task<string?> GetInfiniteCampusBaseUrlAsync();
        Task<string?> GetInfiniteCampusUsernameAsync();
        Task<string?> GetInfiniteCampusPasswordAsync();
        Task SaveCanvasBaseUrlAsync(string url);
        Task SaveCanvasApiTokenAsync(string token);
        Task SaveOpenAiApiKeyAsync(string key);
        Task SaveInfiniteCampusBaseUrlAsync(string url);
        Task SaveInfiniteCampusUsernameAsync(string username);
        Task SaveInfiniteCampusPasswordAsync(string password);
        Task<bool> IsConfigurationCompleteAsync();
        Task ClearAllCredentialsAsync();
    }

    // ---- Implementation ----

    public class SecureConfigService : ISecureConfigService
    {
        private const string KeyCanvasUrl = "canvas_base_url";
        private const string KeyCanvasToken = "canvas_api_token";
        private const string KeyOpenAiKey = "openai_api_key";
        private const string KeyIcUrl = "ic_base_url";
        private const string KeyIcUser = "ic_username";
        private const string KeyIcPass = "ic_password";

        private readonly ILoggingService _log;

        public SecureConfigService(ILoggingService loggingService)
        {
            _log = loggingService;
        }

        public async Task<string?> GetCanvasBaseUrlAsync() =>
            await GetAsync(KeyCanvasUrl, "EDUAUTO_CANVAS_URL");

        public async Task<string?> GetCanvasApiTokenAsync() =>
            await GetAsync(KeyCanvasToken, "EDUAUTO_CANVAS_TOKEN");

        public async Task<string?> GetOpenAiApiKeyAsync() =>
            await GetAsync(KeyOpenAiKey, "EDUAUTO_OPENAI_KEY");

        public async Task<string?> GetInfiniteCampusBaseUrlAsync() =>
            await GetAsync(KeyIcUrl, "EDUAUTO_IC_URL");

        public async Task<string?> GetInfiniteCampusUsernameAsync() =>
            await GetAsync(KeyIcUser, "EDUAUTO_IC_USER");

        public async Task<string?> GetInfiniteCampusPasswordAsync() =>
            await GetAsync(KeyIcPass, "EDUAUTO_IC_PASS");

        public async Task SaveCanvasBaseUrlAsync(string url) =>
            await SetAsync(KeyCanvasUrl, url.TrimEnd('/'));

        public async Task SaveCanvasApiTokenAsync(string token) =>
            await SetAsync(KeyCanvasToken, token.Trim());

        public async Task SaveOpenAiApiKeyAsync(string key) =>
            await SetAsync(KeyOpenAiKey, key.Trim());

        public async Task SaveInfiniteCampusBaseUrlAsync(string url) =>
            await SetAsync(KeyIcUrl, url.TrimEnd('/'));

        public async Task SaveInfiniteCampusUsernameAsync(string username) =>
            await SetAsync(KeyIcUser, username.Trim());

        public async Task SaveInfiniteCampusPasswordAsync(string password) =>
            await SetAsync(KeyIcPass, password);

        public async Task<bool> IsConfigurationCompleteAsync()
        {
            string? canvasUrl = await GetCanvasBaseUrlAsync();
            string? canvasToken = await GetCanvasApiTokenAsync();
            string? openAiKey = await GetOpenAiApiKeyAsync();

            bool isComplete = !string.IsNullOrWhiteSpace(canvasUrl)
                && !string.IsNullOrWhiteSpace(canvasToken)
                && !string.IsNullOrWhiteSpace(openAiKey);

            _log.LogDebug("SecureConfigService.IsConfigurationCompleteAsync",
                $"Configuration complete: {isComplete}");

            return isComplete;
        }

        public async Task ClearAllCredentialsAsync()
        {
            _log.LogWarning("SecureConfigService.ClearAllCredentialsAsync",
                "Clearing all stored credentials from SecureStorage.");
            try
            {
                SecureStorage.Default.Remove(KeyCanvasUrl);
                SecureStorage.Default.Remove(KeyCanvasToken);
                SecureStorage.Default.Remove(KeyOpenAiKey);
                SecureStorage.Default.Remove(KeyIcUrl);
                SecureStorage.Default.Remove(KeyIcUser);
                SecureStorage.Default.Remove(KeyIcPass);
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _log.LogError("SecureConfigService.ClearAllCredentialsAsync",
                    "Failed to clear credentials from SecureStorage.", ex);
                throw;
            }
        }

        // Tries SecureStorage first, then falls back to environment variables.
        private async Task<string?> GetAsync(string storageKey, string envVarFallback)
        {
            try
            {
                string? stored = await SecureStorage.Default.GetAsync(storageKey);
                if (!string.IsNullOrWhiteSpace(stored))
                    return stored;

                // Fallback to environment variable (development only).
                string? envValue = Environment.GetEnvironmentVariable(envVarFallback);
                if (!string.IsNullOrWhiteSpace(envValue))
                {
                    _log.LogDebug("SecureConfigService.GetAsync",
                        $"Using environment variable fallback for key: {storageKey}");
                    return envValue;
                }

                return null;
            }
            catch (Exception ex)
            {
                _log.LogError("SecureConfigService.GetAsync",
                    $"Failed to retrieve key '{storageKey}' from SecureStorage.", ex);
                return null;
            }
        }

        private async Task SetAsync(string storageKey, string value)
        {
            try
            {
                await SecureStorage.Default.SetAsync(storageKey, value);
                _log.LogDebug("SecureConfigService.SetAsync",
                    $"Credential stored for key: {storageKey}");
            }
            catch (Exception ex)
            {
                _log.LogError("SecureConfigService.SetAsync",
                    $"Failed to store key '{storageKey}' in SecureStorage.", ex);
                throw;
            }
        }
    }
}
