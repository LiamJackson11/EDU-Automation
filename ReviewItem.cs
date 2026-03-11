// Models/ReviewItem.cs
// Represents AI-generated assignment content that is pending user review and approval.
// SECURITY NOTE: An assignment cannot be submitted unless IsApprovedByUser is true.
// This is enforced in the CanvasService.SubmitAssignmentAsync guard clause.

using System;

namespace EduAutomation.Models
{
    public enum ReviewItemStatus
    {
        PendingReview,
        Approved,
        Rejected,
        Submitted,
        SubmissionFailed
    }

    public class ReviewItem
    {
        // Unique identifier for this review item.
        public string ReviewId { get; set; } = Guid.NewGuid().ToString();

        // The source assignment this content is for.
        public Assignment SourceAssignment { get; set; } = new Assignment();

        // The original AI-generated content as returned by OpenAI.
        public string OriginalAiContent { get; set; } = string.Empty;

        // The (potentially edited) content that will be submitted.
        // The user may edit this before approving.
        public string EditedContent { get; set; } = string.Empty;

        // CRITICAL: This must be explicitly set to true by the user tapping Approve.
        // The submission endpoint will throw UnauthorizedSubmissionException if false.
        public bool IsApprovedByUser { get; set; } = false;

        // Tracks the current state of this review item.
        public ReviewItemStatus Status { get; set; } = ReviewItemStatus.PendingReview;

        // The raw prompt that was sent to OpenAI (for audit and debugging).
        public string PromptSentToAi { get; set; } = string.Empty;

        // The OpenAI model used (recorded for traceability).
        public string AiModelUsed { get; set; } = string.Empty;

        // Total tokens consumed by this generation call.
        public int TotalTokensUsed { get; set; } = 0;

        // Timestamp when AI content was generated.
        public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

        // Timestamp when the user approved this item.
        public DateTimeOffset? ApprovedAt { get; set; }

        // Timestamp when this was submitted to Canvas.
        public DateTimeOffset? SubmittedAt { get; set; }

        // Error message if submission failed.
        public string? SubmissionError { get; set; }

        // The content that will actually be submitted. Prioritizes edited content.
        public string FinalContent =>
            string.IsNullOrWhiteSpace(EditedContent) ? OriginalAiContent : EditedContent;
    }
}
