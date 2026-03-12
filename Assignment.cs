// Models/Assignment.cs
// Represents a single assignment from Canvas LMS or Infinite Campus.

namespace EduAutomation.Models
{
    public enum AssignmentStatus
    {
        Missing,
        Late,
        Submitted,
        Graded,
        Upcoming
    }

    public enum AssignmentSource
    {
        Canvas,
        InfiniteCampus
    }

    public class Assignment
    {
        // Unique identifier from the source system.
        public string Id { get; set; } = string.Empty;

        // Human-readable name of the assignment.
        public string Title { get; set; } = string.Empty;

        // Full assignment description or prompt as provided by the teacher.
        public string Description { get; set; } = string.Empty;

        // Name of the course this assignment belongs to.
        public string CourseName { get; set; } = string.Empty;

        // Canvas course ID, used to construct API endpoints.
        public string CourseId { get; set; } = string.Empty;

        // Due date parsed from the source API.
        public DateTimeOffset? DueDate { get; set; }

        // Current status of the assignment.
        public AssignmentStatus Status { get; set; } = AssignmentStatus.Missing;

        // Which platform this assignment was fetched from.
        public AssignmentSource Source { get; set; } = AssignmentSource.Canvas;

        // Maximum possible points for this assignment.
        public double? PointsPossible { get; set; }

        // Direct URL to the assignment in the LMS portal.
        public string AssignmentUrl { get; set; } = string.Empty;

        // Submission type (online_text_entry, online_upload, etc.)
        public string SubmissionType { get; set; } = "online_text_entry";

        // Timestamp of when this record was fetched from the API.
        public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;

        // Whether AI content has been generated for this assignment.
        public bool HasGeneratedContent { get; set; } = false;

        // Human-readable due date string for display in the UI.
        public string DueDateDisplay =>
            DueDate.HasValue
                ? DueDate.Value.ToLocalTime().ToString("ddd, MMM d 'at' h:mm tt")
                : "No due date";

        // Returns true if the assignment is overdue.
        public bool IsOverdue =>
            DueDate.HasValue && DueDate.Value < DateTimeOffset.UtcNow
            && Status != AssignmentStatus.Submitted
            && Status != AssignmentStatus.Graded;
    }
}
