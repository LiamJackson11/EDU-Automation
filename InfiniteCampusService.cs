// Services/IInfiniteCampusService.cs + InfiniteCampusService.cs
// Handles Infinite Campus integration.
// Attempts REST API first; falls back to authenticated HTML scraping
// via HtmlAgilityPack if the district does not expose a REST API.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EduAutomation.Exceptions;
using EduAutomation.Models;
using HtmlAgilityPack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EduAutomation.Services
{
    // ---- Interface ----

    public interface IInfiniteCampusService
    {
        Task<bool> AuthenticateAsync(CancellationToken ct = default);
        Task<List<Assignment>> GetMissingAssignmentsAsync(CancellationToken ct = default);
        Task<bool> IsSessionValidAsync();
        Task SignOutAsync();
    }

    // ---- Implementation ----

    public class InfiniteCampusService : IInfiniteCampusService
    {
        private const string Source = "InfiniteCampusService";
        private const int SessionLifetimeMinutes = 25;

        private readonly HttpClient _httpClient;
        private readonly ILoggingService _log;
        private readonly ISecureConfigService _config;
        private readonly CookieContainer _cookieContainer;

        private bool _isAuthenticated = false;
        private DateTimeOffset _sessionCreatedAt = DateTimeOffset.MinValue;

        public InfiniteCampusService(
            HttpClient httpClient,
            ILoggingService log,
            ISecureConfigService config)
        {
            _httpClient = httpClient;
            _log = log;
            _config = config;
            _cookieContainer = new CookieContainer();
        }

        public async Task<bool> IsSessionValidAsync()
        {
            if (!_isAuthenticated) return false;

            TimeSpan sessionAge = DateTimeOffset.UtcNow - _sessionCreatedAt;
            if (sessionAge.TotalMinutes >= SessionLifetimeMinutes)
            {
                _log.LogInfo(Source,
                    $"IC session is {sessionAge.TotalMinutes:F0} minutes old. Re-authenticating...");
                return await AuthenticateAsync();
            }
            return true;
        }

        public async Task<bool> AuthenticateAsync(CancellationToken ct = default)
        {
            _log.LogInfo(Source, "Authenticating with Infinite Campus...");
            var sw = Stopwatch.StartNew();

            string? baseUrl = await _config.GetInfiniteCampusBaseUrlAsync();
            string? username = await _config.GetInfiniteCampusUsernameAsync();
            string? password = await _config.GetInfiniteCampusPasswordAsync();

            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(username))
            {
                _log.LogError(Source, "Infinite Campus credentials are not configured.");
                return false;
            }

            try
            {
                // Attempt REST API authentication first.
                bool restSuccess = await TryRestAuthAsync(baseUrl, username, password!, ct);
                if (restSuccess)
                {
                    _isAuthenticated = true;
                    _sessionCreatedAt = DateTimeOffset.UtcNow;
                    sw.Stop();
                    _log.LogInfo(Source, $"IC REST auth successful in {sw.ElapsedMilliseconds}ms.");
                    return true;
                }

                // Fallback to form-based login via scraping.
                _log.LogInfo(Source, "REST auth unavailable. Attempting form-based login...");
                bool formSuccess = await TryFormLoginAsync(baseUrl, username, password!, ct);
                if (formSuccess)
                {
                    _isAuthenticated = true;
                    _sessionCreatedAt = DateTimeOffset.UtcNow;
                    sw.Stop();
                    _log.LogInfo(Source, $"IC form login successful in {sw.ElapsedMilliseconds}ms.");
                    return true;
                }

                _log.LogError(Source, "Both REST and form-based login failed for Infinite Campus.");
                return false;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _log.LogError(Source, "Exception during Infinite Campus authentication.", ex);
                return false;
            }
        }

        public async Task<List<Assignment>> GetMissingAssignmentsAsync(CancellationToken ct = default)
        {
            _log.LogInfo(Source, "Fetching missing assignments from Infinite Campus...");

            if (!await IsSessionValidAsync())
            {
                bool reauthed = await AuthenticateAsync(ct);
                if (!reauthed)
                {
                    throw new ServiceUnavailableException("InfiniteCampus",
                        "Cannot fetch missing assignments: Infinite Campus authentication failed.");
                }
            }

            string? baseUrl = await _config.GetInfiniteCampusBaseUrlAsync();
            if (string.IsNullOrWhiteSpace(baseUrl)) return new List<Assignment>();

            // Try REST API first.
            var restResult = await TryGetMissingAssignmentsRestAsync(baseUrl, ct);
            if (restResult != null)
            {
                _log.LogInfo(Source, $"IC REST: found {restResult.Count} missing assignments.");
                return restResult;
            }

            // Fallback to scraping the gradebook page.
            var scrapeResult = await ScrapeGradebookForMissingAsync(baseUrl, ct);
            _log.LogInfo(Source, $"IC Scrape: found {scrapeResult.Count} missing assignments.");
            return scrapeResult;
        }

        public async Task SignOutAsync()
        {
            _log.LogInfo(Source, "Signing out of Infinite Campus.");
            string? baseUrl = await _config.GetInfiniteCampusBaseUrlAsync();
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                try
                {
                    await _httpClient.GetAsync($"{baseUrl}/campus/logoff.jsp",
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.LogError(Source, "Error during IC sign-out (non-critical).", ex);
                }
            }
            _isAuthenticated = false;
            _sessionCreatedAt = DateTimeOffset.MinValue;
        }

        // Attempts Infinite Campus REST API authentication (district-dependent).
        private async Task<bool> TryRestAuthAsync(
            string baseUrl, string username, string password, CancellationToken ct)
        {
            try
            {
                string tokenUrl = $"{baseUrl}/campus/api/token";
                var form = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("grant_type", "password"),
                    new KeyValuePair<string, string>("username", username),
                    new KeyValuePair<string, string>("password", password)
                });

                var sw = Stopwatch.StartNew();
                HttpResponseMessage response = await _httpClient.PostAsync(tokenUrl, form, ct);
                sw.Stop();
                _log.LogApiCall(Source, "POST", tokenUrl, (int)response.StatusCode, sw.ElapsedMilliseconds);

                if (!response.IsSuccessStatusCode) return false;

                string json = await response.Content.ReadAsStringAsync(ct);
                var tokenObj = JObject.Parse(json);
                string? token = tokenObj["access_token"]?.ToString();

                if (!string.IsNullOrWhiteSpace(token))
                {
                    _httpClient.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                _log.LogDebug(Source, $"REST auth attempt failed (expected for many districts): {ex.Message}");
                return false;
            }
        }

        // Form-based login for districts without a REST API.
        private async Task<bool> TryFormLoginAsync(
            string baseUrl, string username, string password, CancellationToken ct)
        {
            try
            {
                string loginUrl = $"{baseUrl}/campus/verify.do";
                var form = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", username),
                    new KeyValuePair<string, string>("password", password),
                    new KeyValuePair<string, string>("appName", "portal"),
                    new KeyValuePair<string, string>("url", "services/homePageFeed.jsp")
                });

                var sw = Stopwatch.StartNew();
                HttpResponseMessage response = await _httpClient.PostAsync(loginUrl, form, ct);
                sw.Stop();
                _log.LogApiCall(Source, "POST", loginUrl, (int)response.StatusCode, sw.ElapsedMilliseconds);

                if (!response.IsSuccessStatusCode) return false;

                string responseBody = await response.Content.ReadAsStringAsync(ct);

                // A successful IC login will NOT contain the word "Invalid" and
                // will redirect to the portal home.
                bool isLoginPage = responseBody.Contains("verify.do")
                    || responseBody.ToLower().Contains("invalid username");

                return !isLoginPage;
            }
            catch (Exception ex)
            {
                _log.LogError(Source, "Form-based login attempt threw an exception.", ex);
                return false;
            }
        }

        // Tries to fetch missing assignments via the IC REST API.
        private async Task<List<Assignment>?> TryGetMissingAssignmentsRestAsync(
            string baseUrl, CancellationToken ct)
        {
            try
            {
                string url = $"{baseUrl}/campus/api/portal/grades/missingassignments";
                var sw = Stopwatch.StartNew();
                HttpResponseMessage response = await _httpClient.GetAsync(url, ct);
                sw.Stop();
                _log.LogApiCall(Source, "GET", url, (int)response.StatusCode, sw.ElapsedMilliseconds);

                if (!response.IsSuccessStatusCode) return null;

                string json = await response.Content.ReadAsStringAsync(ct);
                var items = JArray.Parse(json);
                var result = new List<Assignment>();

                foreach (var item in items)
                {
                    result.Add(new Assignment
                    {
                        Id = item["assignmentID"]?.ToString() ?? Guid.NewGuid().ToString(),
                        Title = item["assignmentName"]?.ToString() ?? "Unknown Assignment",
                        CourseName = item["courseName"]?.ToString() ?? "Unknown Course",
                        DueDate = item["dueDate"] != null
                            ? DateTimeOffset.TryParse(item["dueDate"]!.ToString(), out var d) ? d : null
                            : null,
                        PointsPossible = item["totalPoints"]?.Value<double>(),
                        Status = AssignmentStatus.Missing,
                        Source = AssignmentSource.InfiniteCampus
                    });
                }
                return result;
            }
            catch (Exception ex)
            {
                _log.LogDebug(Source, $"IC REST missing assignments unavailable: {ex.Message}");
                return null;
            }
        }

        // Scrapes the IC gradebook HTML page as a fallback.
        private async Task<List<Assignment>> ScrapeGradebookForMissingAsync(
            string baseUrl, CancellationToken ct)
        {
            var result = new List<Assignment>();
            try
            {
                string url = $"{baseUrl}/campus/portal/student/gradebook.xsl";
                var sw = Stopwatch.StartNew();
                HttpResponseMessage response = await _httpClient.GetAsync(url, ct);
                sw.Stop();
                _log.LogApiCall(Source, "GET", url, (int)response.StatusCode, sw.ElapsedMilliseconds);

                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning(Source,
                        $"Gradebook scrape returned HTTP {(int)response.StatusCode}.");
                    return result;
                }

                string html = await response.Content.ReadAsStringAsync(ct);
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // Look for assignment rows marked as missing.
                // IC uses class="missing" or shows a "M" flag in the score column.
                var missingRows = doc.DocumentNode
                    .SelectNodes("//tr[contains(@class,'missing') or .//td[contains(@class,'missing')]]");

                if (missingRows == null)
                {
                    _log.LogInfo(Source, "No missing assignment rows found in gradebook HTML.");
                    return result;
                }

                foreach (var row in missingRows)
                {
                    string assignmentName = row.SelectSingleNode(
                        ".//td[contains(@class,'assignment')]")?.InnerText.Trim()
                        ?? row.SelectSingleNode(".//td[1]")?.InnerText.Trim()
                        ?? "Unknown Assignment";

                    string courseName = row.SelectSingleNode(
                        ".//td[contains(@class,'course')]")?.InnerText.Trim()
                        ?? "Unknown Course";

                    string dueDateStr = row.SelectSingleNode(
                        ".//td[contains(@class,'due')]")?.InnerText.Trim()
                        ?? string.Empty;

                    DateTimeOffset.TryParse(dueDateStr, out DateTimeOffset dueDate);

                    result.Add(new Assignment
                    {
                        Id = Guid.NewGuid().ToString(),
                        Title = assignmentName,
                        CourseName = courseName,
                        DueDate = dueDateStr != string.Empty ? dueDate : null,
                        Status = AssignmentStatus.Missing,
                        Source = AssignmentSource.InfiniteCampus
                    });
                }

                _log.LogInfo(Source,
                    $"Scraping found {result.Count} missing assignments in gradebook.");
                return result;
            }
            catch (Exception ex)
            {
                _log.LogError(Source, "Error scraping Infinite Campus gradebook.", ex);
                throw new ServiceUnavailableException("InfiniteCampus",
                    "Failed to scrape gradebook from Infinite Campus. " +
                    "The portal HTML structure may have changed.", ex);
            }
        }
    }
}
