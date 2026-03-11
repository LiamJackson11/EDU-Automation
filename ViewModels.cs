// ViewModels/ViewModels.cs
// Contains: DashboardViewModel, GmailViewModel, AssignmentsViewModel,
//           DataDumpViewModel, ReviewViewModel
// All using directives are at the top of the file.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EduAutomation.Exceptions;
using EduAutomation.Models;
using EduAutomation.Services;

namespace EduAutomation.ViewModels
{
    // ============================================================
    // DashboardViewModel
    // ============================================================

    public partial class DashboardViewModel : BaseViewModel
    {
        private readonly ICanvasService _canvas;
        private readonly IInfiniteCampusService _ic;
        private readonly IGmailService _gmail;

        [ObservableProperty] private int _missingAssignmentCount = 0;
        [ObservableProperty] private int _unreadAlertCount = 0;
        [ObservableProperty] private int _pendingReviewCount = 0;
        [ObservableProperty] private string _welcomeMessage = "Good morning, Student!";
        [ObservableProperty] private bool _isGmailConnected = false;
        [ObservableProperty] private bool _isCanvasConnected = false;
        [ObservableProperty] private bool _isIcConnected = false;

        public ObservableCollection<Assignment> RecentMissingAssignments { get; } = new();

        public DashboardViewModel(
            ICanvasService canvas,
            IInfiniteCampusService ic,
            IGmailService gmail,
            ILoggingService log) : base(log)
        {
            _canvas = canvas;
            _ic = ic;
            _gmail = gmail;
            UpdateWelcomeMessage();
        }

        [RelayCommand]
        public async Task RefreshDashboardAsync()
        {
            await RunSafeAsync(async () =>
            {
                Log.LogInfo("DashboardViewModel", "Refreshing dashboard...");
                IsCanvasConnected = await _canvas.ValidateTokenAsync();
                var missing = await _canvas.GetMissingAssignmentsAsync();
                MissingAssignmentCount = missing.Count;
                RecentMissingAssignments.Clear();
                foreach (var item in missing.Take(3))
                    RecentMissingAssignments.Add(item);
                IsGmailConnected = await _gmail.IsAuthenticatedAsync();
                Log.LogInfo("DashboardViewModel", "Dashboard refresh complete.");
            }, "Refreshing dashboard...");
        }

        private void UpdateWelcomeMessage()
        {
            var hour = DateTime.Now.Hour;
            WelcomeMessage = hour switch
            {
                < 12 => "Good morning, Student!",
                < 17 => "Good afternoon, Student!",
                _ => "Good evening, Student!"
            };
        }
    }

    // ============================================================
    // GmailViewModel
    // ============================================================

    public partial class GmailViewModel : BaseViewModel
    {
        private readonly IGmailService _gmail;

        [ObservableProperty] private bool _isAuthenticated = false;
        [ObservableProperty] private EmailAlert? _selectedEmail;

        public ObservableCollection<EmailAlert> Emails { get; } = new();

        public GmailViewModel(IGmailService gmail, ILoggingService log) : base(log)
        {
            _gmail = gmail;
        }

        [RelayCommand]
        public async Task ConnectGmailAsync()
        {
            await RunSafeAsync(async () =>
            {
                Log.LogInfo("GmailViewModel", "Initiating Gmail OAuth flow...");
                IsAuthenticated = await _gmail.AuthenticateAsync();
                if (IsAuthenticated)
                    await LoadEmailsAsync();
            }, "Connecting to Gmail...");
        }

        [RelayCommand]
        public async Task LoadEmailsAsync()
        {
            await RunSafeAsync(async () =>
            {
                Log.LogInfo("GmailViewModel", "Loading school emails...");
                var emails = await _gmail.GetSchoolEmailsAsync(maxResults: 25);
                Emails.Clear();
                foreach (var e in emails)
                    Emails.Add(e);
                Log.LogInfo("GmailViewModel", $"Loaded {Emails.Count} school emails.");
            }, "Loading emails...");
        }

        [RelayCommand]
        public void DisconnectGmail()
        {
            _gmail.SignOut();
            IsAuthenticated = false;
            Emails.Clear();
            Log.LogInfo("GmailViewModel", "Disconnected from Gmail.");
        }
    }

    // ============================================================
    // AssignmentsViewModel
    // ============================================================

    public partial class AssignmentsViewModel : BaseViewModel
    {
        private readonly ICanvasService _canvas;
        private readonly IInfiniteCampusService _ic;
        private readonly IOpenAIService _openAi;

        [ObservableProperty] private Assignment? _selectedAssignment;
        [ObservableProperty] private string _statusMessage = string.Empty;

        public ObservableCollection<Assignment> MissingAssignments { get; } = new();
        public event Action<ReviewItem>? ReviewItemReady;

        public AssignmentsViewModel(
            ICanvasService canvas,
            IInfiniteCampusService ic,
            IOpenAIService openAi,
            ILoggingService log) : base(log)
        {
            _canvas = canvas;
            _ic = ic;
            _openAi = openAi;
        }

        [RelayCommand]
        public async Task LoadMissingAssignmentsAsync()
        {
            await RunSafeAsync(async () =>
            {
                Log.LogInfo("AssignmentsViewModel", "Loading missing assignments from all sources...");
                MissingAssignments.Clear();
                var canvasMissing = await _canvas.GetMissingAssignmentsAsync();
                foreach (var a in canvasMissing)
                    MissingAssignments.Add(a);
                try
                {
                    var icMissing = await _ic.GetMissingAssignmentsAsync();
                    foreach (var a in icMissing)
                        MissingAssignments.Add(a);
                }
                catch
                {
                    Log.LogWarning("AssignmentsViewModel",
                        "Infinite Campus fetch failed. Showing Canvas data only.");
                }
                StatusMessage = $"{MissingAssignments.Count} missing assignment(s) found.";
                Log.LogInfo("AssignmentsViewModel",
                    $"Loaded {MissingAssignments.Count} total missing assignments.");
            }, "Loading missing assignments...");
        }

        [RelayCommand]
        public async Task GenerateAiContentAsync(Assignment assignment)
        {
            if (assignment == null) return;
            await RunSafeAsync(async () =>
            {
                Log.LogInfo("AssignmentsViewModel",
                    $"Generating AI content for: {assignment.Title}");
                StatusMessage = $"Generating content for '{assignment.Title}'...";
                var reviewItem = await _openAi.GenerateAssignmentResponseAsync(
                    assignment, new List<string>());
                assignment.HasGeneratedContent = true;
                StatusMessage = "Content generated. Sent to Review tab.";
                ReviewItemReady?.Invoke(reviewItem);
                Log.LogInfo("AssignmentsViewModel",
                    $"Review item {reviewItem.ReviewId} sent to Review tab.");
            }, "Generating AI content...");
        }
    }

    // ============================================================
    // DataDumpViewModel
    // ============================================================

    public partial class DataDumpViewModel : BaseViewModel
    {
        private readonly ICanvasService _canvas;
        private readonly IOpenAIService _openAi;
        private readonly HttpClient _httpClient;

        [ObservableProperty] private string _inputText = string.Empty;
        [ObservableProperty] private DataDumpType _selectedInputType = DataDumpType.RawText;
        [ObservableProperty] private string _statusMessage = string.Empty;
        [ObservableProperty] private Assignment? _linkedAssignment;

        public ObservableCollection<DataDumpItem> DumpItems { get; } = new();
        public ObservableCollection<Assignment> AvailableAssignments { get; } = new();
        public event Action<ReviewItem>? ReviewItemReady;

        public DataDumpViewModel(
            ICanvasService canvas,
            IOpenAIService openAi,
            HttpClient httpClient,
            ILoggingService log) : base(log)
        {
            _canvas = canvas;
            _openAi = openAi;
            _httpClient = httpClient;
        }

        [RelayCommand]
        public async Task AddDataItemAsync()
        {
            if (string.IsNullOrWhiteSpace(InputText)) return;
            await RunSafeAsync(async () =>
            {
                string content = InputText.Trim();
                if (SelectedInputType == DataDumpType.GoogleDocsUrl
                    || SelectedInputType == DataDumpType.PastedLink)
                {
                    content = await FetchLinkContentAsync(content);
                }
                var item = new DataDumpItem
                {
                    RawContent = InputText.Trim(),
                    ResolvedContent = content,
                    InputType = SelectedInputType,
                    LinkedAssignmentId = LinkedAssignment?.Id,
                    IsProcessed = true
                };
                DumpItems.Add(item);
                InputText = string.Empty;
                StatusMessage = $"Data item added. Total: {DumpItems.Count} item(s).";
                Log.LogInfo("DataDumpViewModel", $"Added data dump item of type {item.InputType}.");
            }, "Processing input...");
        }

        [RelayCommand]
        public void RemoveItem(DataDumpItem item)
        {
            DumpItems.Remove(item);
            StatusMessage = $"{DumpItems.Count} item(s) in data dump.";
        }

        [RelayCommand]
        public async Task GenerateWithDumpDataAsync()
        {
            if (LinkedAssignment == null)
            {
                ShowError("Select an assignment to link before generating.");
                return;
            }
            if (DumpItems.Count == 0)
            {
                ShowError("Add at least one data item before generating.");
                return;
            }
            await RunSafeAsync(async () =>
            {
                var supplementalData = DumpItems
                    .Where(i => i.IsProcessed)
                    .Select(i => i.ResolvedContent ?? i.RawContent)
                    .ToList();
                var reviewItem = await _openAi.GenerateAssignmentResponseAsync(
                    LinkedAssignment, supplementalData);
                StatusMessage = "Content generated with your data. Check the Review tab.";
                ReviewItemReady?.Invoke(reviewItem);
                Log.LogInfo("DataDumpViewModel",
                    $"Generated review item {reviewItem.ReviewId} with {supplementalData.Count} data inputs.");
            }, "Generating with your data...");
        }

        [RelayCommand]
        public async Task LoadAssignmentsAsync()
        {
            await RunSafeAsync(async () =>
            {
                var assignments = await _canvas.GetMissingAssignmentsAsync();
                AvailableAssignments.Clear();
                foreach (var a in assignments) AvailableAssignments.Add(a);
            }, "Loading assignments...");
        }

        private async Task<string> FetchLinkContentAsync(string url)
        {
            try
            {
                Log.LogInfo("DataDumpViewModel", $"Fetching content from URL: {url}");
                string content = await _httpClient.GetStringAsync(url);
                return content.Length > 5000 ? content[..5000] + "... [truncated]" : content;
            }
            catch (Exception ex)
            {
                Log.LogError("DataDumpViewModel", $"Failed to fetch URL: {url}", ex);
                return $"[Could not retrieve content from: {url}]";
            }
        }
    }

    // ============================================================
    // ReviewViewModel
    // CRITICAL: Controls the approval workflow. No submission can occur
    // without explicit user approval via ApproveContentCommand.
    // ============================================================

    public partial class ReviewViewModel : BaseViewModel
    {
        private readonly ICanvasService _canvas;

        [ObservableProperty] private ReviewItem? _selectedReviewItem;
        [ObservableProperty] private string _statusMessage = string.Empty;
        [ObservableProperty] private bool _canSubmit = false;

        public ObservableCollection<ReviewItem> PendingReviewItems { get; } = new();

        public ReviewViewModel(ICanvasService canvas, ILoggingService log) : base(log)
        {
            _canvas = canvas;
        }

        public void AddReviewItem(ReviewItem item)
        {
            PendingReviewItems.Add(item);
            SelectedReviewItem = item;
            StatusMessage = $"{PendingReviewItems.Count} item(s) pending your review.";
            Log.LogInfo("ReviewViewModel",
                $"Review item {item.ReviewId} added. Assignment: '{item.SourceAssignment.Title}'");
        }

        [RelayCommand]
        public void UpdateEditedContent(string content)
        {
            if (SelectedReviewItem == null) return;
            SelectedReviewItem.EditedContent = content;
        }

        [RelayCommand]
        public void ApproveContent()
        {
            if (SelectedReviewItem == null) return;
            SelectedReviewItem.IsApprovedByUser = true;
            SelectedReviewItem.ApprovedAt = DateTimeOffset.UtcNow;
            SelectedReviewItem.Status = ReviewItemStatus.Approved;
            CanSubmit = true;
            Log.LogInfo("ReviewViewModel",
                $"User APPROVED review item {SelectedReviewItem.ReviewId} " +
                $"for '{SelectedReviewItem.SourceAssignment.Title}' " +
                $"at {SelectedReviewItem.ApprovedAt}");
            StatusMessage = "Content approved. You can now submit to Canvas.";
        }

        [RelayCommand]
        public void RejectContent()
        {
            if (SelectedReviewItem == null) return;
            SelectedReviewItem.IsApprovedByUser = false;
            SelectedReviewItem.Status = ReviewItemStatus.Rejected;
            CanSubmit = false;
            Log.LogInfo("ReviewViewModel",
                $"User REJECTED review item {SelectedReviewItem.ReviewId}.");
            StatusMessage = "Content rejected. Return to Assignments to regenerate.";
        }

        [RelayCommand]
        public async Task SubmitToCanvasAsync()
        {
            if (SelectedReviewItem == null)
            {
                ShowError("No review item selected.");
                return;
            }
            if (!SelectedReviewItem.IsApprovedByUser)
            {
                ShowError("This assignment has not been approved. " +
                          "You must click Approve before submitting.");
                Log.LogError("ReviewViewModel",
                    $"BLOCKED: Attempt to submit unapproved item {SelectedReviewItem.ReviewId}");
                return;
            }
            await RunSafeAsync(async () =>
            {
                Log.LogInfo("ReviewViewModel",
                    $"Submitting review item {SelectedReviewItem.ReviewId} to Canvas...");
                StatusMessage = "Submitting to Canvas...";
                bool success = await _canvas.SubmitAssignmentAsync(SelectedReviewItem);
                if (success)
                {
                    SelectedReviewItem.Status = ReviewItemStatus.Submitted;
                    SelectedReviewItem.SubmittedAt = DateTimeOffset.UtcNow;
                    PendingReviewItems.Remove(SelectedReviewItem);
                    SelectedReviewItem = PendingReviewItems.Count > 0
                        ? PendingReviewItems[0] : null;
                    CanSubmit = false;
                    StatusMessage = "Assignment submitted successfully to Canvas!";
                    Log.LogInfo("ReviewViewModel", "Assignment submitted to Canvas successfully.");
                }
                else
                {
                    SelectedReviewItem!.Status = ReviewItemStatus.SubmissionFailed;
                    StatusMessage = "Submission failed. Check your Canvas connection and try again.";
                }
            }, "Submitting to Canvas...");
        }

        [RelayCommand]
        public void SelectReviewItem(ReviewItem item)
        {
            SelectedReviewItem = item;
            CanSubmit = item.IsApprovedByUser;
        }
    }
}