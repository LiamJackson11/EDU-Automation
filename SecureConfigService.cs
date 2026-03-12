// Services/ISecureConfigService.cs + SecureConfigService.cs
// Stores and retrieves all credentials via .NET MAUI SecureStorage.
// Canvas uses username + password (no API token needed).
// Falls back to environment variables during development.

namespace EduAutomation.Services
{
    // ---- Interface ----

    public interface ISecureConfigService
    {
        // Canvas credentials
        Task<string?> GetCanvasBaseUrlAsync();
        Task<string?> GetCanvasUsernameAsync();
        Task<string?> GetCanvasPasswordAsync();

        // Infinite Campus credentials
        Task<string?> GetInfiniteCampusBaseUrlAsync();
        Task<string?> GetInfiniteCampusUsernameAsync();
        Task<string?> GetInfiniteCampusPasswordAsync();

        // OpenAI
        Task<string?> GetOpenAiApiKeyAsync();

        // Savers
        Task SaveCanvasBaseUrlAsync(string url);
        Task SaveCanvasUsernameAsync(string username);
        Task SaveCanvasPasswordAsync(string password);
        Task SaveInfiniteCampusBaseUrlAsync(string url);
        Task SaveInfiniteCampusUsernameAsync(string username);
        Task SaveInfiniteCampusPasswordAsync(string password);
        Task SaveOpenAiApiKeyAsync(string key);

        Task<bool> IsConfigurationCompleteAsync();
        Task ClearAllCredentialsAsync();
    }

    // ---- Implementation ----

    public class SecureConfigService : ISecureConfigService
    {
        private const string KeyCanvasUrl      = "canvas_base_url";
        private const string KeyCanvasUser     = "canvas_username";
        private const string KeyCanvasPass     = "canvas_password";
        private const string KeyIcUrl          = "ic_base_url";
        private const string KeyIcUser         = "ic_username";
        private const string KeyIcPass         = "ic_password";
        private const string KeyOpenAiKey      = "openai_api_key";

        private readonly ILoggingService _log;

        public SecureConfigService(ILoggingService loggingService)
        {
            _log = loggingService;
        }

        public Task<string?> GetCanvasBaseUrlAsync()        => GetAsync(KeyCanvasUrl,  "EDUAUTO_CANVAS_URL");
        public Task<string?> GetCanvasUsernameAsync()       => GetAsync(KeyCanvasUser, "EDUAUTO_CANVAS_USER");
        public Task<string?> GetCanvasPasswordAsync()       => GetAsync(KeyCanvasPass, "EDUAUTO_CANVAS_PASS");
        public Task<string?> GetInfiniteCampusBaseUrlAsync()=> GetAsync(KeyIcUrl,      "EDUAUTO_IC_URL");
        public Task<string?> GetInfiniteCampusUsernameAsync()=> GetAsync(KeyIcUser,    "EDUAUTO_IC_USER");
        public Task<string?> GetInfiniteCampusPasswordAsync()=> GetAsync(KeyIcPass,    "EDUAUTO_IC_PASS");
        public Task<string?> GetOpenAiApiKeyAsync()         => GetAsync(KeyOpenAiKey,  "EDUAUTO_OPENAI_KEY");

        public Task SaveCanvasBaseUrlAsync(string url)        => SetAsync(KeyCanvasUrl,  url.TrimEnd('/'));
        public Task SaveCanvasUsernameAsync(string username)  => SetAsync(KeyCanvasUser, username.Trim());
        public Task SaveCanvasPasswordAsync(string password)  => SetAsync(KeyCanvasPass, password);
        public Task SaveInfiniteCampusBaseUrlAsync(string url)=> SetAsync(KeyIcUrl,      url.TrimEnd('/'));
        public Task SaveInfiniteCampusUsernameAsync(string u) => SetAsync(KeyIcUser,     u.Trim());
        public Task SaveInfiniteCampusPasswordAsync(string p) => SetAsync(KeyIcPass,     p);
        public Task SaveOpenAiApiKeyAsync(string key)         => SetAsync(KeyOpenAiKey,  key.Trim());

        public async Task<bool> IsConfigurationCompleteAsync()
        {
            string? canvasUrl  = await GetCanvasBaseUrlAsync();
            string? canvasUser = await GetCanvasUsernameAsync();
            string? canvasPass = await GetCanvasPasswordAsync();
            string? openAiKey  = await GetOpenAiApiKeyAsync();

            bool complete = !string.IsNullOrWhiteSpace(canvasUrl)
                         && !string.IsNullOrWhiteSpace(canvasUser)
                         && !string.IsNullOrWhiteSpace(canvasPass)
                         && !string.IsNullOrWhiteSpace(openAiKey);

            _log.LogDebug("SecureConfigService.IsConfigurationCompleteAsync",
                $"Configuration complete: {complete}");
            return complete;
        }

        public async Task ClearAllCredentialsAsync()
        {
            _log.LogWarning("SecureConfigService.ClearAllCredentialsAsync",
                "Clearing all stored credentials.");
            try
            {
                SecureStorage.Default.Remove(KeyCanvasUrl);
                SecureStorage.Default.Remove(KeyCanvasUser);
                SecureStorage.Default.Remove(KeyCanvasPass);
                SecureStorage.Default.Remove(KeyIcUrl);
                SecureStorage.Default.Remove(KeyIcUser);
                SecureStorage.Default.Remove(KeyIcPass);
                SecureStorage.Default.Remove(KeyOpenAiKey);
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _log.LogError("SecureConfigService.ClearAllCredentialsAsync",
                    "Failed to clear credentials.", ex);
                throw;
            }
        }

        private async Task<string?> GetAsync(string storageKey, string envFallback)
        {
            try
            {
                string? stored = await SecureStorage.Default.GetAsync(storageKey);
                if (!string.IsNullOrWhiteSpace(stored)) return stored;

                string? envValue = Environment.GetEnvironmentVariable(envFallback);
                if (!string.IsNullOrWhiteSpace(envValue))
                {
                    _log.LogDebug("SecureConfigService.GetAsync",
                        $"Using env var fallback for: {storageKey}");
                    return envValue;
                }
                return null;
            }
            catch (Exception ex)
            {
                _log.LogError("SecureConfigService.GetAsync",
                    $"Failed to read key '{storageKey}'.", ex);
                return null;
            }
        }

        private async Task SetAsync(string storageKey, string value)
        {
            try
            {
                await SecureStorage.Default.SetAsync(storageKey, value);
                _log.LogDebug("SecureConfigService.SetAsync",
                    $"Saved credential for key: {storageKey}");
            }
            catch (Exception ex)
            {
                _log.LogError("SecureConfigService.SetAsync",
                    $"Failed to save key '{storageKey}'.", ex);
                throw;
            }
        }
    }
}