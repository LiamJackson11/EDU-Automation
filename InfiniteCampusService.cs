// Services/IInfiniteCampusService.cs + InfiniteCampusService.cs
//
// Full web-scraping implementation for Infinite Campus.
// No API keys or tokens required -- only your school login credentials.
//
// Authentication flow:
//   1. GET the login page to locate the form action URL and any hidden inputs.
//   2. POST credentials to the form action (usually /campus/verify.do).
//   3. Session cookie maintained in CookieContainer for all subsequent requests.
//
// Data scraping:
//   - Grades / missing assignments: scrapes /campus/portal/student/grades.xsl
//     and /campus/portal/student/todo.jsp
//   - Falls back to the main dashboard feed JSON endpoint if the school
//     district has it enabled (/campus/api/portal/grades).
//   - Parses the assignment table rows looking for cells that contain the
//     text "M" (missing) or have class="missing" in Infinite Campus themes.
//
// Session management:
//   - IC sessions expire after ~30 minutes; re-auth fires at 25 minutes.
using HtmlAgilityPack;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using EduAutomation.Exceptions;
using EduAutomation.Models;
using Newtonsoft.Json.Linq;

namespace EduAutomation.Services
{
    // ---- Interface ----

    public interface IInfiniteCampusService
    {
        Task<bool> LoginAsync(CancellationToken ct = default);
        Task<bool> IsSessionValidAsync();
        Task<List<Assignment>> GetMissingAssignmentsAsync(CancellationToken ct = default);
        Task SignOutAsync();
    }

    // ---- Implementation ----

    public class InfiniteCampusService : IInfiniteCampusService
    {
        private const string Source             = "InfiniteCampusService";
        private const int    SessionLifetimeMin = 25;

        private readonly HttpClient           _http;
        private readonly ILoggingService      _log;
        private readonly ISecureConfigService _config;

        private bool           _isLoggedIn     = false;
        private DateTimeOffset _sessionCreated = DateTimeOffset.MinValue;
        private string         _baseUrl        = string.Empty;

        public InfiniteCampusService(
            HttpClient http, ILoggingService log, ISecureConfigService config)
        {
            _http   = http;
            _log    = log;
            _config = config;
        }

        // ---- Session guard ----

        public async Task<bool> IsSessionValidAsync()
        {
            if (!_isLoggedIn) return false;
            double ageMin = (DateTimeOffset.UtcNow - _sessionCreated).TotalMinutes;
            if (ageMin >= SessionLifetimeMin)
            {
                _log.LogInfo(Source,
                    $"IC session is {ageMin:F0} min old (limit {SessionLifetimeMin}). Re-authenticating.");
                return await LoginAsync();
            }
            return true;
        }

        // ---- Step 1: Discover the login form ----
        // Different districts host IC at different paths and some customise the
        // form. We GET the root login URL and inspect the HTML to find the
        // correct form action and any hidden/required inputs.

        private async Task<(string formAction, Dictionary<string, string> hiddenFields)>
            DiscoverLoginFormAsync(string loginUrl, CancellationToken ct)
        {
            _log.LogInfo(Source, $"Discovering IC login form at: {loginUrl}");
            var hiddenFields = new Dictionary<string, string>();
            string formAction = loginUrl; // fallback

            try
            {
                HttpResponseMessage resp = await _http.GetAsync(loginUrl, ct);
                resp.EnsureSuccessStatusCode();
                string html = await resp.Content.ReadAsStringAsync(ct);

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // Find the first <form> that contains a password input.
                HtmlNode? loginForm = doc.DocumentNode
                    .SelectNodes("//form")
                    ?.FirstOrDefault(f =>
                        f.SelectSingleNode(".//input[@type='password']") != null);

                if (loginForm != null)
                {
                    string action = loginForm.GetAttributeValue("action", "");
                    if (!string.IsNullOrWhiteSpace(action))
                    {
                        formAction = action.StartsWith("http")
                            ? action
                            : _baseUrl + action;
                    }

                    // Collect all hidden inputs (state tokens, app names, etc.).
                    foreach (HtmlNode input in loginForm
                        .SelectNodes(".//input[@type='hidden']") ?? Enumerable.Empty<HtmlNode>())
                    {
                        string name  = input.GetAttributeValue("name",  "");
                        string value = WebUtility.HtmlDecode(
                            input.GetAttributeValue("value", ""));
                        if (!string.IsNullOrWhiteSpace(name))
                            hiddenFields[name] = value;
                    }
                }

                _log.LogInfo(Source,
                    $"IC login form action: {formAction}. " +
                    $"Hidden fields found: {hiddenFields.Count}");
            }
            catch (Exception ex)
            {
                _log.LogError(Source, "Could not discover IC login form. Using default path.", ex);
            }

            return (formAction, hiddenFields);
        }

        // ---- Step 2: POST credentials ----

        public async Task<bool> LoginAsync(CancellationToken ct = default)
        {
            string? baseUrl  = await _config.GetInfiniteCampusBaseUrlAsync();
            string? username = await _config.GetInfiniteCampusUsernameAsync();
            string? password = await _config.GetInfiniteCampusPasswordAsync();

            if (string.IsNullOrWhiteSpace(baseUrl)
             || string.IsNullOrWhiteSpace(username)
             || string.IsNullOrWhiteSpace(password))
            {
                _log.LogError(Source,
                    "Infinite Campus credentials are not configured. " +
                    "Enter your IC district URL, username, and password in Settings.");
                return false;
            }

            _baseUrl = baseUrl.TrimEnd('/');

            // Try the two most common IC login page paths.
            string[] loginPaths =
            {
                $"{_baseUrl}/campus/portal/students.jsp",
                $"{_baseUrl}/campus/students/portal.jsp",
                $"{_baseUrl}/campus/verify.do",
                $"{_baseUrl}/campus"
            };

            foreach (string loginUrl in loginPaths)
            {
                bool success = await TryLoginAtUrlAsync(loginUrl, username, password, ct);
                if (success)
                {
                    _isLoggedIn     = true;
                    _sessionCreated = DateTimeOffset.UtcNow;
                    _log.LogInfo(Source,
                        $"IC login succeeded via {loginUrl} at {_sessionCreated:g}");
                    return true;
                }
            }

            _log.LogError(Source,
                "All IC login URL paths failed. Verify your district URL in Settings. " +
                "The URL should be the base of your school's IC portal (e.g. " +
                "https://district.infinitecampus.com).");
            return false;
        }

        private async Task<bool> TryLoginAtUrlAsync(
            string loginUrl, string username, string password, CancellationToken ct)
        {
            _log.LogInfo(Source, $"Attempting IC login at: {loginUrl}");
            var sw = Stopwatch.StartNew();
            try
            {
                var (formAction, hiddenFields) = await DiscoverLoginFormAsync(loginUrl, ct);

                // Build the POST body. Start with discovered hidden fields, then add credentials.
                var formData = new List<KeyValuePair<string, string>>();
                foreach (var kv in hiddenFields)
                    formData.Add(new(kv.Key, kv.Value));

                // Add standard IC credential field names.
                // IC uses 'username' and 'password' on most deployments.
                formData.Add(new("username", username));
                formData.Add(new("password", password));

                // Some deployments also need 'appName' and 'url'.
                if (!hiddenFields.ContainsKey("appName"))
                    formData.Add(new("appName", "portal"));
                if (!hiddenFields.ContainsKey("url"))
                    formData.Add(new("url", "services/homePageFeed.jsp"));

                var content = new FormUrlEncodedContent(formData);
                HttpResponseMessage resp = await _http.PostAsync(formAction, content, ct);
                sw.Stop();
                _log.LogApiCall(Source, "POST", formAction,
                    (int)resp.StatusCode, sw.ElapsedMilliseconds);

                string body    = await resp.Content.ReadAsStringAsync(ct);
                string finalUrl= resp.RequestMessage?.RequestUri?.ToString() ?? string.Empty;

                // Failure signatures: redirected back to login, or error text present.
                bool failed =
                    finalUrl.Contains("verify.do") ||
                    finalUrl.Contains("login") ||
                    body.Contains("Invalid username", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("Invalid password", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("please try again", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase);

                if (failed)
                {
                    _log.LogDebug(Source,
                        $"IC login at {loginUrl} failed (redirected or error text found). " +
                        $"Final URL: {finalUrl}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _log.LogDebug(Source, $"IC login attempt at {loginUrl} threw: {ex.Message}");
                return false;
            }
        }

        public async Task SignOutAsync()
        {
            _log.LogInfo(Source, "Signing out of Infinite Campus.");
            try { await _http.GetAsync($"{_baseUrl}/campus/logoff.jsp"); }
            catch (Exception ex)
            {
                _log.LogError(Source, "IC sign-out error (non-critical).", ex);
            }
            _isLoggedIn     = false;
            _sessionCreated = DateTimeOffset.MinValue;
        }

        // ---- Get missing assignments ----
        // Tries three approaches in order of reliability.

        public async Task<List<Assignment>> GetMissingAssignmentsAsync(CancellationToken ct = default)
        {
            _log.LogInfo(Source, "Fetching missing assignments from Infinite Campus...");
            if (!await EnsureSessionAsync(ct))
                throw new ServiceUnavailableException("InfiniteCampus",
                    "Cannot fetch missing assignments: IC authentication failed. " +
                    "Check your credentials in Settings.");

            // Approach 1: district REST API (enabled by some schools).
            var apiResult = await TryGetMissingViaApiAsync(ct);
            if (apiResult != null)
            {
                _log.LogInfo(Source, $"IC REST API returned {apiResult.Count} missing items.");
                return apiResult;
            }

            // Approach 2: scrape the To-Do / planner page.
            var todoResult = await ScrapeToDoPageAsync(ct);
            if (todoResult.Count > 0)
            {
                _log.LogInfo(Source, $"IC To-Do page returned {todoResult.Count} missing items.");
                return todoResult;
            }

            // Approach 3: scrape the gradebook page for missing score flags.
            var gradebookResult = await ScrapeGradebookAsync(ct);
            _log.LogInfo(Source,
                $"IC Gradebook scrape returned {gradebookResult.Count} missing items.");
            return gradebookResult;
        }

        // ---- Approach 1: district REST API ----

        private async Task<List<Assignment>?> TryGetMissingViaApiAsync(CancellationToken ct)
        {
            string[] candidateUrls =
            {
                $"{_baseUrl}/campus/api/portal/grades/missingassignments",
                $"{_baseUrl}/campus/resources/portal/grades/missingassignments",
            };

            foreach (string url in candidateUrls)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    HttpResponseMessage resp = await _http.GetAsync(url, ct);
                    sw.Stop();
                    _log.LogApiCall(Source, "GET", url, (int)resp.StatusCode, sw.ElapsedMilliseconds);

                    if (!resp.IsSuccessStatusCode) continue;

                    string json = await resp.Content.ReadAsStringAsync(ct);
                    JToken parsed = JToken.Parse(json);
                    JArray items = parsed is JArray arr ? arr
                        : parsed["data"] as JArray ?? new JArray();

                    var result = new List<Assignment>();
                    foreach (JToken item in items)
                    {
                        result.Add(new Assignment
                        {
                            Id         = item["assignmentID"]?.ToString()
                                       ?? item["id"]?.ToString()
                                       ?? Guid.NewGuid().ToString(),
                            Title      = item["assignmentName"]?.ToString()
                                       ?? item["name"]?.ToString()
                                       ?? "Unknown Assignment",
                            CourseName = item["courseName"]?.ToString()
                                       ?? item["course"]?.ToString()
                                       ?? "Unknown Course",
                            DueDate    = TryParseDate(item["dueDate"]?.ToString()
                                       ?? item["due"]?.ToString()),
                            PointsPossible = item["totalPoints"]?.Value<double?>()
                                          ?? item["points"]?.Value<double?>(),
                            Status = AssignmentStatus.Missing,
                            Source = AssignmentSource.InfiniteCampus
                        });
                    }
                    return result;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(Source, $"IC API at {url} unavailable: {ex.Message}");
                }
            }
            return null;
        }

        // ---- Approach 2: scrape the To-Do / planner page ----

        private async Task<List<Assignment>> ScrapeToDoPageAsync(CancellationToken ct)
        {
            var result = new List<Assignment>();
            string[] todoUrls =
            {
                $"{_baseUrl}/campus/portal/student/todo.jsp",
                $"{_baseUrl}/campus/portal/student/planner.jsp",
                $"{_baseUrl}/campus/student/planner",
            };

            foreach (string url in todoUrls)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    HttpResponseMessage resp = await _http.GetAsync(url, ct);
                    sw.Stop();
                    _log.LogApiCall(Source, "GET", url, (int)resp.StatusCode, sw.ElapsedMilliseconds);

                    if (!resp.IsSuccessStatusCode) continue;

                    string html = await resp.Content.ReadAsStringAsync(ct);
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    // IC To-Do page typically renders a list of tasks with course name,
                    // assignment name, and due date inside <li> or <tr> elements.
                    var assignmentNodes = doc.DocumentNode
                        .SelectNodes("//li[contains(@class,'task') or contains(@class,'assignment')]"
                                   + " | //tr[contains(@class,'task') or contains(@class,'missing')]");

                    if (assignmentNodes == null || !assignmentNodes.Any()) continue;

                    foreach (HtmlNode node in assignmentNodes)
                    {
                        string title = (
                            node.SelectSingleNode(".//*[contains(@class,'title')]")
                            ?? node.SelectSingleNode(".//*[contains(@class,'name')]")
                            ?? node.SelectSingleNode(".//a")
                            ?? node)?.InnerText.Trim() ?? string.Empty;

                        string course = (
                            node.SelectSingleNode(".//*[contains(@class,'course')]")
                            ?? node.SelectSingleNode(".//*[contains(@class,'class')]"))
                            ?.InnerText.Trim() ?? "Unknown Course";

                        string dueTxt = (
                            node.SelectSingleNode(".//*[contains(@class,'due')]")
                            ?? node.SelectSingleNode(".//*[contains(@class,'date')]"))
                            ?.InnerText.Trim() ?? string.Empty;

                        if (string.IsNullOrWhiteSpace(title)) continue;

                        result.Add(new Assignment
                        {
                            Id         = Guid.NewGuid().ToString(),
                            Title      = title,
                            CourseName = course,
                            DueDate    = TryParseDate(dueTxt),
                            Status     = AssignmentStatus.Missing,
                            Source     = AssignmentSource.InfiniteCampus
                        });
                    }

                    if (result.Count > 0)
                    {
                        _log.LogInfo(Source,
                            $"Scraped {result.Count} items from IC To-Do page: {url}");
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(Source, $"IC To-Do scrape at {url} failed: {ex.Message}");
                }
            }
            return result;
        }

        // ---- Approach 3: scrape the gradebook ----

        private async Task<List<Assignment>> ScrapeGradebookAsync(CancellationToken ct)
        {
            var result = new List<Assignment>();
            string[] gradebookUrls =
            {
                $"{_baseUrl}/campus/portal/student/grades.xsl",
                $"{_baseUrl}/campus/student/grades",
                $"{_baseUrl}/campus/grades/student",
            };

            foreach (string url in gradebookUrls)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    HttpResponseMessage resp = await _http.GetAsync(url, ct);
                    sw.Stop();
                    _log.LogApiCall(Source, "GET", url, (int)resp.StatusCode, sw.ElapsedMilliseconds);

                    if (!resp.IsSuccessStatusCode) continue;

                    string html = await resp.Content.ReadAsStringAsync(ct);
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    result.AddRange(ParseGradebookHtml(doc, url));
                    if (result.Count > 0)
                    {
                        _log.LogInfo(Source,
                            $"Gradebook scrape found {result.Count} missing assignments at {url}");
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(Source, $"Gradebook scrape at {url} failed: {ex.Message}");
                }
            }

            if (result.Count == 0)
                _log.LogWarning(Source,
                    "All IC scraping approaches returned 0 results. " +
                    "Verify your district URL is correct in Settings. " +
                    "The IC portal structure may differ slightly from the expected layout.");

            return result;
        }

        private List<Assignment> ParseGradebookHtml(HtmlDocument doc, string sourceUrl)
        {
            var result = new List<Assignment>();

            // Strategy A: rows with class="missing" or a cell containing "M" score flag.
            var missingRows = doc.DocumentNode.SelectNodes(
                "//tr[contains(@class,'missing')" +
                " or .//td[contains(@class,'missing')]" +
                " or .//td[@title='Missing']" +
                " or .//td[normalize-space(text())='M']]");

            if (missingRows != null)
            {
                foreach (HtmlNode row in missingRows)
                {
                    string title = ExtractCellText(row,
                        "td[contains(@class,'assignment')]",
                        "td[contains(@class,'name')]",
                        "td[1]");

                    string course = ExtractCellText(row,
                        "td[contains(@class,'course')]",
                        "td[contains(@class,'class')]",
                        "");

                    string dueText = ExtractCellText(row,
                        "td[contains(@class,'due')]",
                        "td[contains(@class,'date')]",
                        "");

                    if (!string.IsNullOrWhiteSpace(title))
                        result.Add(new Assignment
                        {
                            Id         = Guid.NewGuid().ToString(),
                            Title      = title,
                            CourseName = string.IsNullOrWhiteSpace(course)
                                       ? "Unknown Course" : course,
                            DueDate    = TryParseDate(dueText),
                            Status     = AssignmentStatus.Missing,
                            Source     = AssignmentSource.InfiniteCampus
                        });
                }
                return result;
            }

            // Strategy B: look for any table that has an "Assignment" header column
            // and scan rows for empty or zero scores.
            var tables = doc.DocumentNode.SelectNodes("//table");
            if (tables == null) return result;

            foreach (HtmlNode table in tables)
            {
                var headers = table.SelectNodes(".//th");
                if (headers == null) continue;

                int assignmentCol = -1, courseCol = -1, dueCol = -1, scoreCol = -1;
                for (int i = 0; i < headers.Count; i++)
                {
                    string h = headers[i].InnerText.Trim().ToLower();
                    if (h.Contains("assignment") || h.Contains("task"))   assignmentCol = i;
                    else if (h.Contains("course") || h.Contains("class")) courseCol = i;
                    else if (h.Contains("due"))                            dueCol = i;
                    else if (h.Contains("score") || h.Contains("grade"))  scoreCol = i;
                }

                if (assignmentCol < 0) continue;

                foreach (HtmlNode row in table.SelectNodes(".//tr[td]") ?? Enumerable.Empty<HtmlNode>())
                {
                    var cells = row.SelectNodes("td");
                    if (cells == null || cells.Count <= assignmentCol) continue;

                    string scoreText = scoreCol >= 0 && scoreCol < cells.Count
                        ? cells[scoreCol].InnerText.Trim() : "";

                    // A row is "missing" if the score cell is empty, "M", "--", or "0/X".
                    bool isMissing =
                        string.IsNullOrWhiteSpace(scoreText) ||
                        scoreText == "M" || scoreText == "--" ||
                        Regex.IsMatch(scoreText, @"^0\s*/\s*\d+$");

                    if (!isMissing) continue;

                    string title  = cells[assignmentCol].InnerText.Trim();
                    string course = courseCol >= 0 && courseCol < cells.Count
                        ? cells[courseCol].InnerText.Trim() : "Unknown Course";
                    string due    = dueCol >= 0 && dueCol < cells.Count
                        ? cells[dueCol].InnerText.Trim() : "";

                    if (!string.IsNullOrWhiteSpace(title))
                        result.Add(new Assignment
                        {
                            Id         = Guid.NewGuid().ToString(),
                            Title      = title,
                            CourseName = course,
                            DueDate    = TryParseDate(due),
                            Status     = AssignmentStatus.Missing,
                            Source     = AssignmentSource.InfiniteCampus
                        });
                }
            }

            return result;
        }

        // ---- Helpers ----

        private async Task<bool> EnsureSessionAsync(CancellationToken ct)
        {
            if (await IsSessionValidAsync()) return true;
            return await LoginAsync(ct);
        }

        private static string ExtractCellText(HtmlNode row, params string[] xpaths)
        {
            foreach (string xpath in xpaths)
            {
                if (string.IsNullOrWhiteSpace(xpath)) continue;
                HtmlNode? node = row.SelectSingleNode($".//{xpath}");
                if (node != null)
                {
                    string text = node.InnerText.Trim();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
            return string.Empty;
        }

        private static DateTimeOffset? TryParseDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return DateTimeOffset.TryParse(raw, out DateTimeOffset result) ? result : null;
        }
    }
}