// Models/DataModels.cs
// Contains: EmailAlert, DataDumpType, DataDumpItem, CourseInfo

namespace EduAutomation.Models
{
    // ============================================================
    // EmailAlert
    // ============================================================

    public class EmailAlert
    {
        public string MessageId        { get; set; } = string.Empty;
        public string ThreadId         { get; set; } = string.Empty;
        public string Subject          { get; set; } = string.Empty;
        public string Sender           { get; set; } = string.Empty;
        public string SnippetPreview   { get; set; } = string.Empty;
        public string FullBodyText     { get; set; } = string.Empty;
        public DateTimeOffset ReceivedAt { get; set; }
        public bool IsRead             { get; set; } = false;
        public bool IsSchoolRelated    { get; set; } = false;
        public List<string> Labels     { get; set; } = new();
        public string ReceivedDisplay  =>
            ReceivedAt.ToLocalTime().ToString("MMM d, h:mm tt");
    }

    // ============================================================
    // DataDumpType / DataDumpItem
    // ============================================================

    public enum DataDumpType
    {
        RawText,
        GoogleDocsUrl,
        VoiceTranscript,
        PastedLink
    }

    public class DataDumpItem
    {
        public string ItemId              { get; set; } = Guid.NewGuid().ToString();
        public string RawContent          { get; set; } = string.Empty;
        public DataDumpType InputType     { get; set; } = DataDumpType.RawText;
        public string? ResolvedContent    { get; set; }
        public DateTimeOffset AddedAt     { get; set; } = DateTimeOffset.UtcNow;
        public bool IsProcessed           { get; set; } = false;
        public string? LinkedAssignmentId { get; set; }

        public string DisplayLabel => InputType switch
        {
            DataDumpType.GoogleDocsUrl   => "Google Doc Link",
            DataDumpType.VoiceTranscript => "Voice Transcript",
            DataDumpType.PastedLink      => "Pasted URL",
            _                            => "Raw Text"
        };
    }

    // ============================================================
    // CourseInfo
    // ============================================================

    public class CourseInfo
    {
        public string CourseId     { get; set; } = string.Empty;
        public string CourseName   { get; set; } = string.Empty;
        public string CourseCode   { get; set; } = string.Empty;
        public string TeacherName  { get; set; } = string.Empty;
        public bool IsActive       { get; set; } = true;
        public DateTimeOffset? StartDate { get; set; }
        public DateTimeOffset? EndDate   { get; set; }
        public int MissingAssignmentCount { get; set; } = 0;
        public List<Assignment> Assignments { get; set; } = new();
    }
}