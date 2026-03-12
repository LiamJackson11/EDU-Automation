// Services/IGmailService.cs + GmailService.cs
// Handles Gmail API integration using Google.Apis.Gmail.v1.
// Scans for school-related emails using configurable filter keywords.
// Uses OAuth 2.0 with automatic token refresh via Google.Apis.Auth.

using System.Diagnostics;
using System.Text;
using EduAutomation.Exceptions;
using EduAutomation.Models;

namespace EduAutomation.Services
{
    // ---- Interface ----

    public interface IGmailService
    {
        Task<bool> AuthenticateAsync(CancellationToken ct = default);
        Task<List<EmailAlert>> GetSchoolEmailsAsync(int maxResults = 20, CancellationToken ct = default);
        Task<bool> IsAuthenticatedAsync();
        void SignOut();
    }

    // ---- Implementation ----

    public class GmailService : IGmailService
    {
        private const string Source          = "GmailService";
        private const string ApplicationName = "EduAutomation";
        private const string UserId          = "me";

        // OAuth scopes required. Read-only to minimize permissions.
        private static readonly string[] Scopes = { Google.Apis.Gmail.v1.GmailService.Scope.GmailReadonly };

        // Keywords used to identify school-related emails.
        private static readonly string[] SchoolKeywords = new[]
        {
            "canvas", "lms", "assignment", "grade", "gradebook", "missing", "late",
            "teacher", "school", "class", "homework", "project", "test", "exam",
            "quiz", "progress report", "infinite campus", "academic", "syllabus"
        };

        private readonly ILoggingService _log;
        private Google.Apis.Gmail.v1.GmailService? _service;
        private UserCredential? _credential;
        private string _credentialStorePath;

        public GmailService(ILoggingService log)
        {
            _log = log;
            _credentialStorePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EduAutomation",
                "GoogleCredentials");
            Directory.CreateDirectory(_credentialStorePath);
        }

        public async Task<bool> AuthenticateAsync(CancellationToken ct = default)
        {
            _log.LogInfo(Source, "Starting Google OAuth authentication flow...");
            var sw = Stopwatch.StartNew();

            try
            {
                // Load the OAuth client secrets from embedded resources.
                // The google_credentials.json file must be in Resources/Raw/.
                using Stream credStream = await GetCredentialStreamAsync();

                _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    GoogleClientSecrets.FromStream(credStream).Secrets,
                    Scopes,
                    user: "student",
                    taskCancellationToken: ct,
                    dataStore: new FileDataStore(_credentialStorePath, fullPath: true));

                // Pre-emptively refresh the token if it is within 5 minutes of expiry.
                if (_credential.Token.IsStale)
                {
                    _log.LogInfo(Source, "Token is stale. Refreshing before use...");
                    bool refreshed = await _credential.RefreshTokenAsync(ct);
                    if (!refreshed)
                    {
                        _log.LogError(Source, "Token refresh failed. Re-authentication required.");
                        return false;
                    }
                    _log.LogInfo(Source, "Token refreshed successfully.");
                }

                _service = new Google.Apis.Gmail.v1.GmailService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = _credential,
                    ApplicationName      = ApplicationName
                });

                sw.Stop();
                _log.LogInfo(Source, $"Gmail authentication successful in {sw.ElapsedMilliseconds}ms.");
                return true;
            }
            catch (Google.Apis.Auth.OAuth2.Responses.TokenResponseException ex)
            {
                _log.LogError(Source, $"Google OAuth token error: {ex.Error.Error}", ex);
                return false;
            }
            catch (Exception ex)
            {
                _log.LogError(Source, "Unexpected error during Gmail authentication.", ex);
                return false;
            }
        }

        public async Task<bool> IsAuthenticatedAsync()
        {
            if (_credential == null || _service == null) return false;
            if (!_credential.Token.IsStale) return true;

            _log.LogInfo(Source, "Refreshing stale Gmail OAuth token...");
            try
            {
                return await _credential.RefreshTokenAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(Source, "Failed to refresh Gmail token.", ex);
                return false;
            }
        }

        public void SignOut()
        {
            _log.LogInfo(Source, "Signing out of Gmail and clearing stored credentials.");
            try
            {
                // BUG FIX: Was calling _credential.RevokeTokenAsync(...).Wait(5000),
                // which blocks the calling thread. In a MAUI UI context this can
                // deadlock because the async continuation needs the UI thread to
                // resume. Use GetAwaiter().GetResult() on a background-safe path,
                // or fire-and-forget with a timeout via Task.Run.
                if (_credential != null)
                {
                    Task.Run(() => _credential.RevokeTokenAsync(CancellationToken.None))
                        .Wait(TimeSpan.FromSeconds(5));
                }

                string tokenFile = Path.Combine(_credentialStorePath,
                    "Google.Apis.Auth.OAuth2.Responses.TokenResponse-student");
                if (File.Exists(tokenFile)) File.Delete(tokenFile);
                _service    = null;
                _credential = null;
            }
            catch (Exception ex)
            {
                _log.LogError(Source, "Error during Gmail sign-out.", ex);
            }
        }

        public async Task<List<EmailAlert>> GetSchoolEmailsAsync(
            int maxResults = 20,
            CancellationToken ct = default)
        {
            _log.LogInfo(Source, $"Fetching up to {maxResults} school-related emails...");
            var result = new List<EmailAlert>();
            var sw = Stopwatch.StartNew();

            if (!await IsAuthenticatedAsync())
            {
                _log.LogError(Source, "Cannot fetch emails: not authenticated with Gmail.");
                throw new ServiceUnavailableException("Gmail",
                    "Gmail authentication required. Please connect your Google account in Settings.");
            }

            try
            {
                // Build a Gmail query to filter for potentially school-related emails.
                string query = "is:unread newer_than:7d";

                var listRequest = _service!.Users.Messages.List(UserId);
                listRequest.Q          = query;
                listRequest.MaxResults = maxResults * 3; // Fetch extra since we filter by keyword.

                ListMessagesResponse listResponse = await listRequest.ExecuteAsync(ct);
                sw.Stop();
                _log.LogApiCall(Source, "GET", "users.messages.list", 200, sw.ElapsedMilliseconds);

                if (listResponse.Messages == null || !listResponse.Messages.Any())
                {
                    _log.LogInfo(Source, "No new emails found matching the query.");
                    return result;
                }

                // Fetch each message's details and filter for school content.
                int processed = 0;
                foreach (var messageRef in listResponse.Messages)
                {
                    if (result.Count >= maxResults) break;
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var msgSw = Stopwatch.StartNew();
                        var getRequest = _service.Users.Messages.Get(UserId, messageRef.Id);
                        getRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
                        Message message = await getRequest.ExecuteAsync(ct);
                        msgSw.Stop();
                        _log.LogApiCall(Source, "GET", $"users.messages.get/{messageRef.Id}",
                            200, msgSw.ElapsedMilliseconds);

                        EmailAlert? alert = ParseMessage(message);
                        if (alert != null && IsSchoolRelated(alert))
                        {
                            alert.IsSchoolRelated = true;
                            result.Add(alert);
                        }
                        processed++;
                    }
                    catch (Google.GoogleApiException ex) when ((int)ex.HttpStatusCode == 429)
                    {
                        _log.LogWarning(Source,
                            $"Gmail rate limit hit after {processed} messages. " +
                            "Returning partial results. Retry in 1 minute.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(Source,
                            $"Failed to fetch message ID {messageRef.Id}. Skipping.", ex);
                    }
                }

                result = result.OrderByDescending(e => e.ReceivedAt).ToList();
                _log.LogInfo(Source,
                    $"Returned {result.Count} school-related emails from {processed} processed.");

                return result;
            }
            catch (Exception ex)
            {
                _log.LogError(Source, "Error fetching emails from Gmail.", ex);
                throw new ServiceUnavailableException("Gmail",
                    "Failed to retrieve emails from Gmail.", ex);
            }
        }

        private EmailAlert? ParseMessage(Message message)
        {
            try
            {
                var headers = message.Payload?.Headers ?? new List<MessagePartHeader>();
                string subject = headers.FirstOrDefault(h => h.Name == "Subject")?.Value ?? "(No Subject)";
                string from    = headers.FirstOrDefault(h => h.Name == "From")?.Value   ?? "Unknown Sender";
                string dateStr = headers.FirstOrDefault(h => h.Name == "Date")?.Value   ?? string.Empty;

                DateTimeOffset receivedAt = DateTimeOffset.TryParse(dateStr, out var parsed)
                    ? parsed : DateTimeOffset.UtcNow;

                string bodyText = ExtractBodyText(message.Payload);

                return new EmailAlert
                {
                    MessageId      = message.Id,
                    ThreadId       = message.ThreadId,
                    Subject        = subject,
                    Sender         = from,
                    SnippetPreview = message.Snippet ?? string.Empty,
                    FullBodyText   = bodyText,
                    ReceivedAt     = receivedAt,
                    IsRead         = !(message.LabelIds?.Contains("UNREAD") ?? false),
                    Labels         = message.LabelIds?.ToList() ?? new List<string>()
                };
            }
            catch (Exception ex)
            {
                _log.LogError(Source, $"Failed to parse message {message.Id}.", ex);
                return null;
            }
        }

        private static string ExtractBodyText(MessagePart? part)
        {
            if (part == null) return string.Empty;
            if (part.Body?.Data != null)
            {
                // BUG FIX: The Gmail API returns URL-safe base64 (RFC 4648 §5) which
                // replaces '+' with '-' and '/' with '_', and may omit '=' padding.
                // Convert.FromBase64String requires standard base64 WITH padding.
                // Previously missing the padding step caused FormatException for any
                // message whose body length was not a multiple of 3 bytes.
                string base64 = part.Body.Data.Replace('-', '+').Replace('_', '/');
                int pad = base64.Length % 4;
                if (pad > 0)
                    base64 = base64.PadRight(base64.Length + (4 - pad), '=');

                byte[] data = Convert.FromBase64String(base64);
                return Encoding.UTF8.GetString(data);
            }
            if (part.Parts != null)
            {
                var textPart = part.Parts.FirstOrDefault(p =>
                    p.MimeType == "text/plain" || p.MimeType == "text/html");
                return ExtractBodyText(textPart);
            }
            return string.Empty;
        }

        private static bool IsSchoolRelated(EmailAlert alert)
        {
            string combined = (alert.Subject + " " + alert.SnippetPreview + " " + alert.Sender)
                .ToLowerInvariant();
            return SchoolKeywords.Any(keyword => combined.Contains(keyword));
        }

        // Loads the google_credentials.json from the app's raw assets.
        private static async Task<Stream> GetCredentialStreamAsync()
        {
            // In .NET MAUI, bundled raw assets are accessed via FileSystem.OpenAppPackageFileAsync.
            try
            {
                return await Microsoft.Maui.Storage.FileSystem.OpenAppPackageFileAsync(
                    "google_credentials.json");
            }
            catch (Exception ex)
            {
                throw new ServiceUnavailableException("Gmail",
                    "google_credentials.json not found in app package. " +
                    "Ensure it is added to Resources/Raw/ with Build Action = MauiAsset.", ex);
            }
        }
    }
}