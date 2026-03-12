// Services/CanvasService.cs
// Web-scraping Canvas login + session-cookie authenticated API calls.
// No API token required. Uses username + password only.
using HtmlAgilityPack;
using System.Diagnostics;
using System.Net;
using System.Text;
using EduAutomation.Exceptions;
using EduAutomation.Models;
using Newtonsoft.Json;

namespace EduAutomation.Services
{
    // ---- Interface ----

    public interface ICanvasService
    {
        Task<bool>              LoginAsync(CancellationToken ct = default);
        Task<bool>              IsSessionValidAsync();
        Task<List<CourseInfo>>  GetActiveCoursesAsync(CancellationToken ct = default);
        Task<List<Assignment>>  GetMissingAssignmentsAsync(CancellationToken ct = default);
        Task<List<Assignment>>  GetAssignmentsForCourseAsync(string courseId, CancellationToken ct = default);
        Task<bool>              SubmitAssignmentAsync(ReviewItem reviewItem, CancellationToken ct = default);
        Task                    SignOutAsync();
    }

    // ---- JSON DTOs ----

    internal class CanvasCourseDto
    {
        [JsonProperty("id")]            public long    Id            { get; set; }
        [JsonProperty("name")]          public string  Name          { get; set; } = string.Empty;
        [JsonProperty("course_code")]   public string  CourseCode    { get; set; } = string.Empty;
        [JsonProperty("workflow_state")]public string  WorkflowState { get; set; } = string.Empty;
        [JsonProperty("start_at")]      public DateTimeOffset? StartAt { get; set; }
        [JsonProperty("end_at")]        public DateTimeOffset? EndAt   { get; set; }
    }

    internal class CanvasAssignmentDto
    {
        [JsonProperty("id")]                        public long    Id              { get; set; }
        [JsonProperty("name")]                      public string  Name            { get; set; } = string.Empty;
        [JsonProperty("description")]               public string? Description     { get; set; }
        [JsonProperty("due_at")]                    public DateTimeOffset? DueAt   { get; set; }
        [JsonProperty("points_possible")]           public double? PointsPossible  { get; set; }
        [JsonProperty("html_url")]                  public string  HtmlUrl         { get; set; } = string.Empty;
        [JsonProperty("submission_types")]          public List<string> SubmissionTypes { get; set; } = new();
        [JsonProperty("has_submitted_submissions")] public bool    HasSubmissions  { get; set; }
        // BUG FIX: Added missing course_id field. The Canvas API returns this on
        // /missing_submissions and /courses/{id}/assignments. Without it,
        // MapToAssignment could never resolve a course name from the courseMap.
        [JsonProperty("course_id")]                 public long    CourseId        { get; set; }
    }

    // ---- Implementation ----

    public class CanvasService : ICanvasService
    {
        private const string Source             = "CanvasService";
        private const int    SessionLifetimeMin = 55;

        private readonly HttpClient           _http;
        private readonly ILoggingService      _log;
        private readonly ISecureConfigService _config;

        private bool           _isLoggedIn     = false;
        private DateTimeOffset _sessionCreated = DateTimeOffset.MinValue;
        private string         _baseUrl        = string.Empty;
        private string         _csrfToken      = string.Empty;

        public CanvasService(
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
                _log.LogInfo(Source, $"Canvas session is {ageMin:F0} min old. Re-authenticating.");
                return await LoginAsync();
            }
            return true;
        }

        // ---- Step 1: GET login page and harvest CSRF token ----

        private async Task<string> FetchCsrfTokenAsync(string pageUrl, CancellationToken ct)
        {
            _log.LogInfo(Source, $"Fetching Canvas page to harvest CSRF token: {pageUrl}");
            var sw = Stopwatch.StartNew();
            try
            {
                HttpResponseMessage resp = await _http.GetAsync(pageUrl, ct);
                sw.Stop();
                _log.LogApiCall(Source, "GET", pageUrl, (int)resp.StatusCode, sw.ElapsedMilliseconds);
                resp.EnsureSuccessStatusCode();

                string html = await resp.Content.ReadAsStringAsync(ct);

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // Canvas embeds the CSRF token in a hidden input named 'authenticity_token'.
                HtmlNode tokenNode = doc.DocumentNode
                    .SelectSingleNode("//input[@name='authenticity_token']");

                if (tokenNode != null)
                {
                    string token = WebUtility.HtmlDecode(
                        tokenNode.GetAttributeValue("value", string.Empty));
                    _log.LogInfo(Source, "CSRF token harvested from hidden input.");
                    return token;
                }

                // Some Canvas instances embed it in a <meta name="csrf-token"> tag.
                HtmlNode metaNode = doc.DocumentNode
                    .SelectSingleNode("//meta[@name='csrf-token']");

                if (metaNode != null)
                {
                    string token = WebUtility.HtmlDecode(
                        metaNode.GetAttributeValue("content", string.Empty));
                    _log.LogInfo(Source, "CSRF token harvested from meta tag.");
                    return token;
                }

                _log.LogWarning(Source,
                    "No CSRF token found on Canvas login page. Attempting login without it.");
                return string.Empty;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _log.LogError(Source, "Failed to fetch Canvas login page for CSRF token.", ex);
                return string.Empty;
            }
        }

        // ---- Step 2: POST credentials ----

        public async Task<bool> LoginAsync(CancellationToken ct = default)
        {
            string? baseUrl  = await _config.GetCanvasBaseUrlAsync();
            string? username = await _config.GetCanvasUsernameAsync();
            string? password = await _config.GetCanvasPasswordAsync();

            if (string.IsNullOrWhiteSpace(baseUrl)
             || string.IsNullOrWhiteSpace(username)
             || string.IsNullOrWhiteSpace(password))
            {
                _log.LogError(Source,
                    "Canvas credentials are not configured. " +
                    "Enter your Canvas school URL, username, and password in Settings.");
                return false;
            }

            _baseUrl = baseUrl.TrimEnd('/');
            string loginUrl = $"{_baseUrl}/login/canvas";

            _csrfToken = await FetchCsrfTokenAsync(loginUrl, ct);

            _log.LogInfo(Source, $"Attempting Canvas login for user: {username}");
            var sw = Stopwatch.StartNew();

            try
            {
                var formData = new List<KeyValuePair<string, string>>
                {
                    new("pseudonym_session[unique_id]",   username),
                    new("pseudonym_session[password]",    password),
                    new("pseudonym_session[remember_me]", "0"),
                    new("redirect_to_ssl",                "0"),
                    new("authenticity_token",             _csrfToken)
                };

                HttpResponseMessage resp = await _http.PostAsync(
                    loginUrl, new FormUrlEncodedContent(formData), ct);
                sw.Stop();
                _log.LogApiCall(Source, "POST", loginUrl, (int)resp.StatusCode, sw.ElapsedMilliseconds);

                string body     = await resp.Content.ReadAsStringAsync(ct);
                string finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? string.Empty;

                bool failed =
                    finalUrl.Contains("/login") ||
                    body.Contains("Invalid username",  StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("Invalid password",  StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("please try again",  StringComparison.OrdinalIgnoreCase);

                if (failed)
                {
                    _log.LogError(Source,
                        "Canvas login failed. Server returned login page again. " +
                        $"Final URL: {finalUrl}. Check username/password in Settings.");
                    return false;
                }

                _isLoggedIn     = true;
                _sessionCreated = DateTimeOffset.UtcNow;
                _log.LogInfo(Source,
                    $"Canvas login successful in {sw.ElapsedMilliseconds}ms.");
                return true;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _log.LogError(Source, "Exception during Canvas login POST.", ex);
                return false;
            }
        }

        public async Task SignOutAsync()
        {
            _log.LogInfo(Source, "Signing out of Canvas.");
            try { await _http.DeleteAsync($"{_baseUrl}/logout"); }
            catch (Exception ex)
            {
                _log.LogError(Source, "Canvas sign-out error (non-critical).", ex);
            }
            _isLoggedIn     = false;
            _sessionCreated = DateTimeOffset.MinValue;
            // BUG FIX: Clear URL and token state on sign-out so a subsequent
            // login to a different URL starts clean.
            _baseUrl        = string.Empty;
            _csrfToken      = string.Empty;
        }

        // ---- Active courses ----

        public async Task<List<CourseInfo>> GetActiveCoursesAsync(CancellationToken ct = default)
        {
            _log.LogInfo(Source, "Fetching active Canvas courses...");
            var result = new List<CourseInfo>();
            if (!await EnsureSessionAsync(ct)) return result;

            string url = $"{_baseUrl}/api/v1/courses?enrollment_state=active&per_page=50";
            var sw = Stopwatch.StartNew();
            try
            {
                HttpResponseMessage resp = await _http.GetAsync(url, ct);
                sw.Stop();
                _log.LogApiCall(Source, "GET", url, (int)resp.StatusCode, sw.ElapsedMilliseconds);
                resp.EnsureSuccessStatusCode();

                var courses = JsonConvert.DeserializeObject<List<CanvasCourseDto>>(
                    await resp.Content.ReadAsStringAsync(ct)) ?? new();

                result = courses
                    .Where(c => c.WorkflowState == "available")
                    .Select(c => new CourseInfo
                    {
                        CourseId   = c.Id.ToString(),
                        CourseName = c.Name,
                        CourseCode = c.CourseCode,
                        IsActive   = true,
                        StartDate  = c.StartAt,
                        EndDate    = c.EndAt
                    }).ToList();

                _log.LogInfo(Source, $"Fetched {result.Count} active courses.");
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _log.LogError(Source, "Error fetching Canvas courses.", ex);
                throw new ServiceUnavailableException("Canvas",
                    "Failed to retrieve courses. Check your login.", ex);
            }
        }

        // ---- Missing assignments ----

        public async Task<List<Assignment>> GetMissingAssignmentsAsync(CancellationToken ct = default)
        {
            _log.LogInfo(Source, "Fetching missing assignments from Canvas...");
            var result = new List<Assignment>();
            if (!await EnsureSessionAsync(ct)) return result;

            string url = $"{_baseUrl}/api/v1/users/self/missing_submissions" +
                         "?per_page=100&include[]=course";
            var sw = Stopwatch.StartNew();
            try
            {
                HttpResponseMessage resp = await _http.GetAsync(url, ct);
                sw.Stop();
                _log.LogApiCall(Source, "GET", url, (int)resp.StatusCode, sw.ElapsedMilliseconds);

                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _log.LogWarning(Source, "Canvas 401 on missing_submissions. Re-logging in.");
                    _isLoggedIn = false;
                    if (!await LoginAsync(ct)) return result;
                    resp = await _http.GetAsync(url, ct);
                }

                resp.EnsureSuccessStatusCode();

                var assignments = JsonConvert.DeserializeObject<List<CanvasAssignmentDto>>(
                    await resp.Content.ReadAsStringAsync(ct)) ?? new();

                var courses    = await GetActiveCoursesAsync(ct);
                var courseMap  = courses.ToDictionary(c => c.CourseId, c => c.CourseName);

                result = assignments.Select(a => MapToAssignment(a, courseMap)).ToList();
                _log.LogInfo(Source, $"Found {result.Count} missing assignments.");
                return result;
            }
            catch (ServiceUnavailableException) { throw; }
            catch (Exception ex)
            {
                sw.Stop();
                _log.LogError(Source, "Error fetching Canvas missing assignments.", ex);
                throw new ServiceUnavailableException("Canvas",
                    "Failed to retrieve missing assignments from Canvas.", ex);
            }
        }

        // ---- Assignments for a course ----

        public async Task<List<Assignment>> GetAssignmentsForCourseAsync(
            string courseId, CancellationToken ct = default)
        {
            _log.LogInfo(Source, $"Fetching assignments for course: {courseId}");
            var result = new List<Assignment>();
            if (!await EnsureSessionAsync(ct)) return result;

            string url = $"{_baseUrl}/api/v1/courses/{courseId}/assignments" +
                         "?per_page=50&order_by=due_at";
            var sw = Stopwatch.StartNew();
            try
            {
                HttpResponseMessage resp = await _http.GetAsync(url, ct);
                sw.Stop();
                _log.LogApiCall(Source, "GET", url, (int)resp.StatusCode, sw.ElapsedMilliseconds);
                resp.EnsureSuccessStatusCode();

                var assignments = JsonConvert.DeserializeObject<List<CanvasAssignmentDto>>(
                    await resp.Content.ReadAsStringAsync(ct)) ?? new();

                // Build a single-entry course map so the name resolves correctly
                // for assignments retrieved from a known course endpoint.
                var courseMap = new Dictionary<string, string> { { courseId, courseId } };

                // Fetch the actual name if possible (best-effort, non-fatal).
                try
                {
                    var courses = await GetActiveCoursesAsync(ct);
                    foreach (var c in courses)
                        courseMap[c.CourseId] = c.CourseName;
                }
                catch { /* non-fatal */ }

                result = assignments
                    .Select(a => MapToAssignment(a, courseMap))
                    .ToList();

                _log.LogInfo(Source, $"Fetched {result.Count} assignments for course {courseId}.");
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _log.LogError(Source, $"Error fetching assignments for course {courseId}.", ex);
                throw;
            }
        }

        // ---- Submit assignment (REQUIRES explicit user approval) ----

        public async Task<bool> SubmitAssignmentAsync(ReviewItem reviewItem, CancellationToken ct = default)
        {
            // CRITICAL SAFETY GUARD: never submit without explicit approval.
            if (!reviewItem.IsApprovedByUser)
                throw new UnauthorizedSubmissionException(
                    reviewItem.SourceAssignment.Id, reviewItem.ReviewId);

            if (!await EnsureSessionAsync(ct)) return false;

            string courseId     = reviewItem.SourceAssignment.CourseId;
            string assignmentId = reviewItem.SourceAssignment.Id;

            _log.LogInfo(Source,
                $"Submitting approved assignment '{assignmentId}' (course '{courseId}'). " +
                $"ReviewItem: {reviewItem.ReviewId}. Approved at: {reviewItem.ApprovedAt}");

            // Refresh CSRF token from the assignment page before POSTing.
            string freshCsrf = await FetchCsrfTokenAsync(
                $"{_baseUrl}/courses/{courseId}/assignments/{assignmentId}", ct);
            if (!string.IsNullOrWhiteSpace(freshCsrf))
                _csrfToken = freshCsrf;

            string url = $"{_baseUrl}/api/v1/courses/{courseId}" +
                         $"/assignments/{assignmentId}/submissions";

            var jsonPayload = JsonConvert.SerializeObject(new
            {
                submission = new
                {
                    submission_type = "online_text_entry",
                    body            = reviewItem.FinalContent
                }
            });

            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            // BUG FIX: X-CSRF-Token is a *request* header, not a content header.
            // Previously it was added via httpContent.Headers.TryAddWithoutValidation
            // which writes to HttpContentHeaders — those headers are sent as entity-body
            // headers (e.g. Content-Type), not HTTP request headers. The CSRF token was
            // therefore never actually delivered to the Canvas server, causing all
            // submissions to be rejected with 422/403.
            // Fix: build a full HttpRequestMessage and set the header there.
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = httpContent
            };
            if (!string.IsNullOrWhiteSpace(_csrfToken))
                requestMessage.Headers.TryAddWithoutValidation("X-CSRF-Token", _csrfToken);

            var sw = Stopwatch.StartNew();
            try
            {
                HttpResponseMessage resp = await _http.SendAsync(requestMessage, ct);
                sw.Stop();
                _log.LogApiCall(Source, "POST", url, (int)resp.StatusCode, sw.ElapsedMilliseconds);

                if (resp.IsSuccessStatusCode)
                {
                    _log.LogInfo(Source, $"Assignment '{assignmentId}' submitted successfully.");
                    return true;
                }

                string errorBody = await resp.Content.ReadAsStringAsync(ct);
                _log.LogError(Source,
                    $"Canvas submission failed HTTP {(int)resp.StatusCode}. Body: {errorBody}");
                return false;
            }
            catch (UnauthorizedSubmissionException) { throw; }
            catch (Exception ex)
            {
                sw.Stop();
                _log.LogError(Source, $"Exception submitting assignment '{assignmentId}'.", ex);
                throw new ServiceUnavailableException("Canvas",
                    $"Network error while submitting assignment '{assignmentId}'.", ex);
            }
        }

        // ---- Private helpers ----

        private async Task<bool> EnsureSessionAsync(CancellationToken ct)
        {
            if (await IsSessionValidAsync()) return true;
            return await LoginAsync(ct);
        }

        private static Assignment MapToAssignment(
            CanvasAssignmentDto a, Dictionary<string, string> courseMap)
        {
            // BUG FIX: Was using a.Id.ToString() (the *assignment* ID) as the key,
            // but courseMap is keyed by *course* ID. The lookup always missed, so
            // CourseName was always "Canvas Course". Now uses a.CourseId (added to
            // the DTO above) to correctly resolve the human-readable course name.
            courseMap.TryGetValue(a.CourseId.ToString(), out string? courseName);
            return new Assignment
            {
                Id             = a.Id.ToString(),
                Title          = a.Name,
                Description    = a.Description ?? string.Empty,
                CourseId       = a.CourseId.ToString(),
                CourseName     = courseName ?? "Canvas Course",
                DueDate        = a.DueAt,
                PointsPossible = a.PointsPossible,
                AssignmentUrl  = a.HtmlUrl,
                SubmissionType = a.SubmissionTypes.FirstOrDefault() ?? "online_text_entry",
                Status         = AssignmentStatus.Missing,
                Source         = AssignmentSource.Canvas
            };
        }
    }
}