// Services/ICanvasService.cs + CanvasService.cs
// Handles all communication with the Canvas LMS REST API.
// Canvas API documentation: https://canvas.instructure.com/doc/api/

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

    public interface ICanvasService
    {
        Task<List<CourseInfo>> GetActiveCoursesAsync(CancellationToken ct = default);
        Task<List<Assignment>> GetMissingAssignmentsAsync(CancellationToken ct = default);
        Task<List<Assignment>> GetAssignmentsForCourseAsync(string courseId, CancellationToken ct = default);
        Task<bool> SubmitAssignmentAsync(ReviewItem reviewItem, CancellationToken ct = default);
        Task<bool> ValidateTokenAsync(CancellationToken ct = default);
    }

    // ---- JSON DTO Classes ----

    internal class CanvasCourse
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; } = string.Empty;
        [JsonProperty("course_code")] public string CourseCode { get; set; } = string.Empty;
        [JsonProperty("workflow_state")] public string WorkflowState { get; set; } = string.Empty;
        [JsonProperty("start_at")] public DateTimeOffset? StartAt { get; set; }
        [JsonProperty("end_at")] public DateTimeOffset? EndAt { get; set; }
    }

    internal class CanvasAssignment
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; } = string.Empty;
        [JsonProperty("description")] public string Description { get; set; } = string.Empty;
        [JsonProperty("due_at")] public DateTimeOffset? DueAt { get; set; }
        [JsonProperty("points_possible")] public double? PointsPossible { get; set; }
        [JsonProperty("html_url")] public string HtmlUrl { get; set; } = string.Empty;
        [JsonProperty("submission_types")] public List<string> SubmissionTypes { get; set; } = new();
        [JsonProperty("has_submitted_submissions")] public bool HasSubmissions { get; set; }
    }

    internal class CanvasSubmission
    {
        [JsonProperty("assignment_id")] public long AssignmentId { get; set; }
        [JsonProperty("workflow_state")] public string WorkflowState { get; set; } = string.Empty;
        [JsonProperty("missing")] public bool Missing { get; set; }
        [JsonProperty("late")] public bool Late { get; set; }
        [JsonProperty("submitted_at")] public DateTimeOffset? SubmittedAt { get; set; }
        [JsonProperty("score")] public double? Score { get; set; }
    }

    // ---- Implementation ----

    public class CanvasService : ICanvasService
    {
        private readonly HttpClient _httpClient;
        private readonly ILoggingService _log;
        private readonly ISecureConfigService _config;
        private const string Source = "CanvasService";

        public CanvasService(
            HttpClient httpClient,
            ILoggingService log,
            ISecureConfigService config)
        {
            _httpClient = httpClient;
            _log = log;
            _config = config;
        }

        // Configures the HttpClient with the auth header for this request.
        private async Task<bool> EnsureAuthHeaderAsync()
        {
            string? token = await _config.GetCanvasApiTokenAsync();
            string? baseUrl = await _config.GetCanvasBaseUrlAsync();

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(baseUrl))
            {
                _log.LogError(Source, "Canvas API token or base URL is not configured.");
                return false;
            }

            _httpClient.BaseAddress ??= new Uri(baseUrl.TrimEnd('/') + "/api/v1/");
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            return true;
        }

        public async Task<bool> ValidateTokenAsync(CancellationToken ct = default)
        {
            _log.LogInfo(Source, "Validating Canvas API token...");
            var sw = Stopwatch.StartNew();
            try
            {
                if (!await EnsureAuthHeaderAsync()) return false;

                HttpResponseMessage response = await _httpClient.GetAsync("users/self", ct);
                sw.Stop();
                _log.LogApiCall(Source, "GET", "users/self", (int)response.StatusCode, sw.ElapsedMilliseconds);

                if (response.IsSuccessStatusCode)
                {
                    _log.LogInfo(Source, "Canvas token is valid.");
                    return true;
                }

                _log.LogWarning(Source,
                    $"Canvas token validation failed with HTTP {(int)response.StatusCode}.");
                return false;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _log.LogError(Source, "Exception during Canvas token validation.", ex);
                return false;
            }
        }

        public async Task<List<CourseInfo>> GetActiveCoursesAsync(CancellationToken ct = default)
        {
            _log.LogInfo(Source, "Fetching active Canvas courses...");
            var result = new List<CourseInfo>();
            var sw = Stopwatch.StartNew();

            try
            {
                if (!await EnsureAuthHeaderAsync()) return result;

                string url = "courses?enrollment_state=active&per_page=50";
                HttpResponseMessage response = await _httpClient.GetAsync(url, ct);
                sw.Stop();
                _log.LogApiCall(Source, "GET", url, (int)response.StatusCode, sw.ElapsedMilliseconds);
                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync(ct);
                var courses = JsonConvert.DeserializeObject<List<CanvasCourse>>(json)
                    ?? new List<CanvasCourse>();

                result = courses
                    .Where(c => c.WorkflowState == "available")
                    .Select(c => new CourseInfo
                    {
                        CourseId = c.Id.ToString(),
                        CourseName = c.Name,
                        CourseCode = c.CourseCode,
                        IsActive = true,
                        StartDate = c.StartAt,
                        EndDate = c.EndAt
                    }).ToList();

                _log.LogInfo(Source, $"Fetched {result.Count} active courses.");
                return result;
            }
            catch (HttpRequestException ex)
            {
                _log.LogError(Source, "HTTP error fetching courses from Canvas.", ex);
                throw new ServiceUnavailableException("Canvas",
                    "Failed to retrieve courses from Canvas LMS. Check your token and internet connection.", ex);
            }
            catch (Exception ex)
            {
                _log.LogError(Source, "Unexpected error fetching Canvas courses.", ex);
                throw;
            }
        }

        public async Task<List<Assignment>> GetMissingAssignmentsAsync(CancellationToken ct = default)
        {
            _log.LogInfo(Source, "Fetching missing assignments from Canvas...");
            var result = new List<Assignment>();
            var sw = Stopwatch.StartNew();

            try
            {
                if (!await EnsureAuthHeaderAsync()) return result;

                // Canvas returns missing submissions via the submissions endpoint.
                string url = "users/self/missing_submissions?per_page=100&include[]=course";
                HttpResponseMessage response = await _httpClient.GetAsync(url, ct);
                sw.Stop();
                _log.LogApiCall(Source, "GET", url, (int)response.StatusCode, sw.ElapsedMilliseconds);
                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync(ct);
                var assignments = JsonConvert.DeserializeObject<List<CanvasAssignment>>(json)
                    ?? new List<CanvasAssignment>();

                result = assignments.Select(a => MapToAssignment(a, "Unknown Course")).ToList();

                _log.LogInfo(Source, $"Found {result.Count} missing assignments in Canvas.");
                return result;
            }
            catch (HttpRequestException ex)
            {
                _log.LogError(Source, "HTTP error fetching missing assignments from Canvas.", ex);
                throw new ServiceUnavailableException("Canvas",
                    "Failed to retrieve missing assignments from Canvas LMS.", ex);
            }
            catch (Exception ex)
            {
                _log.LogError(Source, "Unexpected error fetching Canvas missing assignments.", ex);
                throw;
            }
        }

        public async Task<List<Assignment>> GetAssignmentsForCourseAsync(
            string courseId,
            CancellationToken ct = default)
        {
            _log.LogInfo(Source, $"Fetching assignments for Canvas course ID: {courseId}");
            var result = new List<Assignment>();
            var sw = Stopwatch.StartNew();

            try
            {
                if (!await EnsureAuthHeaderAsync()) return result;

                string url = $"courses/{courseId}/assignments?per_page=50&order_by=due_at";
                HttpResponseMessage response = await _httpClient.GetAsync(url, ct);
                sw.Stop();
                _log.LogApiCall(Source, "GET", url, (int)response.StatusCode, sw.ElapsedMilliseconds);
                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync(ct);
                var assignments = JsonConvert.DeserializeObject<List<CanvasAssignment>>(json)
                    ?? new List<CanvasAssignment>();

                result = assignments.Select(a => MapToAssignment(a, courseId)).ToList();

                _log.LogInfo(Source, $"Fetched {result.Count} assignments for course {courseId}.");
                return result;
            }
            catch (Exception ex)
            {
                _log.LogError(Source, $"Error fetching assignments for course {courseId}.", ex);
                throw;
            }
        }

        public async Task<bool> SubmitAssignmentAsync(ReviewItem reviewItem, CancellationToken ct = default)
        {
            // CRITICAL SAFETY GUARDRAIL: Never submit without explicit user approval.
            if (!reviewItem.IsApprovedByUser)
            {
                throw new UnauthorizedSubmissionException(
                    reviewItem.SourceAssignment.Id,
                    reviewItem.ReviewId);
            }

            string assignmentId = reviewItem.SourceAssignment.Id;
            string courseId = reviewItem.SourceAssignment.CourseId;

            _log.LogInfo(Source,
                $"Submitting approved assignment '{assignmentId}' for course '{courseId}'. " +
                $"Review item: {reviewItem.ReviewId}. Approved at: {reviewItem.ApprovedAt}");

            var sw = Stopwatch.StartNew();

            try
            {
                if (!await EnsureAuthHeaderAsync()) return false;

                // Build the submission payload. Supports online_text_entry format.
                var payload = new
                {
                    submission = new
                    {
                        submission_type = "online_text_entry",
                        body = reviewItem.FinalContent
                    }
                };

                string json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                string url = $"courses/{courseId}/assignments/{assignmentId}/submissions";

                HttpResponseMessage response = await _httpClient.PostAsync(url, content, ct);
                sw.Stop();
                _log.LogApiCall(Source, "POST", url, (int)response.StatusCode, sw.ElapsedMilliseconds);

                if (response.IsSuccessStatusCode)
                {
                    _log.LogInfo(Source,
                        $"Assignment '{assignmentId}' submitted successfully to Canvas.");
                    return true;
                }

                string errorBody = await response.Content.ReadAsStringAsync(ct);
                _log.LogError(Source,
                    $"Canvas submission failed with HTTP {(int)response.StatusCode}. Response: {errorBody}");
                return false;
            }
            catch (UnauthorizedSubmissionException)
            {
                // Re-throw safety exceptions without wrapping.
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _log.LogError(Source,
                    $"Exception while submitting assignment '{assignmentId}' to Canvas.", ex);
                throw new ServiceUnavailableException("Canvas",
                    $"Failed to submit assignment '{assignmentId}' due to a network error.", ex);
            }
        }

        private static Assignment MapToAssignment(CanvasAssignment source, string courseName) =>
            new Assignment
            {
                Id = source.Id.ToString(),
                Title = source.Name,
                Description = source.Description ?? string.Empty,
                CourseName = courseName,
                DueDate = source.DueAt,
                PointsPossible = source.PointsPossible,
                AssignmentUrl = source.HtmlUrl,
                SubmissionType = source.SubmissionTypes.FirstOrDefault() ?? "online_text_entry",
                Status = AssignmentStatus.Missing,
                Source = AssignmentSource.Canvas
            };
    }
}
