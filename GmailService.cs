// Services/IGmailService.cs + GmailService.cs
using System.Diagnostics;
using System.Text;
using EduAutomation.Exceptions;
using EduAutomation.Models;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace EduAutomation.Services
{
    public interface IGmailService
    {
        Task<bool> AuthenticateAsync(CancellationToken ct = default);
        Task<List<EmailAlert>> GetSchoolEmailsAsync(int maxResults = 20, CancellationToken ct = default);
        Task<bool> IsAuthenticatedAsync();
        void SignOut();
    }

    public class GmailService : IGmailService
    {
        private const string Source          = "GmailService";
        private const string ApplicationName = "EduAutomation";
        private const string UserId          = "me";

        private static readonly string[] Scopes = { Google.Apis.Gmail.v1.GmailService.Scope.GmailReadonly };

        private static readonly string[] SchoolKeywords =
        {
            "canvas", "lms", "assignment", "grade", "gradebook", "missing", "late",
            "teacher", "school", "class", "homework", "project", "test", "exam",
            "quiz", "progress report", "infinite campus", "academic", "syllabus"
        };

        private readonly ILoggingService _log;
        private Google.Apis.Gmail.v1.GmailService? _service;
        private UserCredential? _credential;
        private readonly string _credentialStorePath;

        public GmailService(ILoggingService log)
        {
            _log = log;
            _credentialStorePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EduAutomation", "GoogleCredentials");
            Directory.CreateDirectory(_credentialStorePath);
        }

        public async Task<bool> AuthenticateAsync(CancellationToken ct = default)
        {
            _log.LogInfo(Source, "Starting Google OAuth authentication flow...");
            var sw = Stopwatch.StartNew();
            try
            {
                using Stream credStream = await GetCredentialStreamAsync();
                _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    GoogleClientSecrets.FromStream(credStream).Secrets,
                    Scopes,
                    user: "student",
                    taskCancellationToken: ct,
                    dataStore: new FileDataStore(_credentialStorePath, fullPath: true));

                if (_credential.Token.IsStale)
                {
                    _log.LogInfo(Source, "Token is stale â€” refreshing...");
                    bool refreshed = await _credential.RefreshTokenAsync(ct);
                    if (!refreshed)
                    {
                        _log.LogError(Source, "Token refresh failed. Re-authentication required.");
                        return false;
                    }
                }

                _service = new Google.Apis.Gmail.v1.GmailService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = _credential,
                    ApplicationName       = ApplicationName
                });

                sw.Stop();
                _log.LogInfo(Source, $"Gmail auth OK in {sw.ElapsedMilliseconds}ms.");
                return true;
            }
            catch (Google.Apis.Auth.OAuth2.Responses.TokenResponseException ex)
            {
                _log.LogError(Source, $"OAuth token error: {ex.Error.Error}", ex);
                return false;
            }
            catch (Exception ex)
            {
                _log.LogError(Source, "Unexpected error during Gmail auth.", ex);
                return false;
            }
        }

        public async Task<bool> IsAuthenticatedAsync()
        {
            if (_credential == null || _service == null) return false;
            if (!_credential.Token.IsStale) return true;
            try { return await _credential.RefreshTokenAsync(CancellationToken.None); }
            catch (Exception ex) { _log.LogError(Source, "Token refresh failed.", ex); return false; }
        }

        public void SignOut()
        {
            _log.LogInfo(Source, "Signing out of Gmail.");
            try
            {
                if (_credential != null)
                    Task.Run(() => _credential.RevokeTokenAsync(CancellationToken.None))
                        .Wait(TimeSpan.FromSeconds(5));

                string tokenFile = Path.Combine(_credentialStorePath,
                    "Google.Apis.Auth.OAuth2.Responses.TokenResponse-student");
                if (File.Exists(tokenFile)) File.Delete(tokenFile);
                _service    = null;
                _credential = null;
            }
            catch (Exception ex) { _log.LogError(Source, "Error during sign-out.", ex); }
        }

        public async Task<List<EmailAlert>> GetSchoolEmailsAsync(
            int maxResults = 20, CancellationToken ct = default)
        {
            _log.LogInfo(Source, $"Fetching up to {maxResults} school emails...");
            var result = new List<EmailAlert>();
            var sw     = Stopwatch.StartNew();

            if (!await IsAuthenticatedAsync())
                throw new ServiceUnavailableException("Gmail",
                    "Gmail authentication required. Connect your Google account in Settings.");

            try
            {
                var listReq = _service!.Users.Messages.List(UserId);
                listReq.Q          = "is:unread newer_than:7d";
                listReq.MaxResults = maxResults * 3;

                ListMessagesResponse listResp = await listReq.ExecuteAsync(ct);
                sw.Stop();
                _log.LogApiCall(Source, "GET", "users.messages.list", 200, sw.ElapsedMilliseconds);

                if (listResp.Messages == null || !listResp.Messages.Any())
                {
                    _log.LogInfo(Source, "No new emails found.");
                    return result;
                }

                int processed = 0;
                foreach (var msgRef in listResp.Messages)
                {
                    if (result.Count >= maxResults) break;
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var getReq = _service.Users.Messages.Get(UserId, msgRef.Id);
                        getReq.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
                        Message msg = await getReq.ExecuteAsync(ct);
                        EmailAlert? alert = ParseMessage(msg);
                        if (alert != null && IsSchoolRelated(alert))
                        { alert.IsSchoolRelated = true; result.Add(alert); }
                        processed++;
                    }
                    catch (Google.GoogleApiException ex) when ((int)ex.HttpStatusCode == 429)
                    {
                        _log.LogWarning(Source, "Gmail rate limit hit. Returning partial results.");
                        break;
                    }
                    catch (Exception ex)
                    { _log.LogError(Source, $"Failed to fetch message {msgRef.Id}.", ex); }
                }

                result = result.OrderByDescending(e => e.ReceivedAt).ToList();
                _log.LogInfo(Source, $"Returned {result.Count} school emails from {processed} processed.");
                return result;
            }
            catch (ServiceUnavailableException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(Source, "Error fetching Gmail messages.", ex);
                throw new ServiceUnavailableException("Gmail", "Failed to retrieve emails.", ex);
            }
        }

        private EmailAlert? ParseMessage(Message message)
        {
            try
            {
                var headers = message.Payload?.Headers ?? new List<MessagePartHeader>();
                string subject  = headers.FirstOrDefault(h => h.Name == "Subject")?.Value ?? "(No Subject)";
                string from     = headers.FirstOrDefault(h => h.Name == "From")?.Value   ?? "Unknown";
                string dateStr  = headers.FirstOrDefault(h => h.Name == "Date")?.Value   ?? string.Empty;
                DateTimeOffset receivedAt = DateTimeOffset.TryParse(dateStr, out var p) ? p : DateTimeOffset.UtcNow;

                return new EmailAlert
                {
                    MessageId      = message.Id,
                    ThreadId       = message.ThreadId,
                    Subject        = subject,
                    Sender         = from,
                    SnippetPreview = message.Snippet ?? string.Empty,
                    FullBodyText   = ExtractBodyText(message.Payload),
                    ReceivedAt     = receivedAt,
                    IsRead         = !(message.LabelIds?.Contains("UNREAD") ?? false),
                    Labels         = message.LabelIds?.ToList() ?? new List<string>()
                };
            }
            catch (Exception ex) { _log.LogError(Source, $"Parse failed for {message.Id}.", ex); return null; }
        }

        private static string ExtractBodyText(MessagePart? part)
        {
            if (part == null) return string.Empty;
            if (part.Body?.Data != null)
            {
                string b64 = part.Body.Data.Replace('-', '+').Replace('_', '/');
                int pad = b64.Length % 4;
                if (pad > 0) b64 = b64.PadRight(b64.Length + (4 - pad), '=');
                return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            }
            if (part.Parts != null)
            {
                var txt = part.Parts.FirstOrDefault(p =>
                    p.MimeType == "text/plain" || p.MimeType == "text/html");
                return ExtractBodyText(txt);
            }
            return string.Empty;
        }

        private static bool IsSchoolRelated(EmailAlert alert)
        {
            string combined = (alert.Subject + " " + alert.SnippetPreview + " " + alert.Sender).ToLowerInvariant();
            return SchoolKeywords.Any(k => combined.Contains(k));
        }

        private static async Task<Stream> GetCredentialStreamAsync()
        {
            try { return await Microsoft.Maui.Storage.FileSystem.OpenAppPackageFileAsync("google_credentials.json"); }
            catch (Exception ex)
            {
                throw new ServiceUnavailableException("Gmail",
                    "google_credentials.json not found in Resources/Raw/. Add it with Build Action = MauiAsset.", ex);
            }
        }
    }
}




















