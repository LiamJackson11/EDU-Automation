// ViewModels/ViewModels.cs
// DashboardViewModel, GmailViewModel, AssignmentsViewModel,
// DataDumpViewModel, ReviewViewModel.
// All using directives are at the top of the file.

using System.Diagnostics;
using System.Collections.ObjectModel;
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
        private readonly ICanvasService          _canvas;
        private readonly IInfiniteCampusService  _ic;
        private readonly IGmailService           _gmail;

        [ObservableProperty] private int    _missingAssignmentCount = 0;
        [ObservableProperty] private int    _unreadAlertCount       = 0;
        [ObservableProperty] private int    _pendingReviewCount     = 0;
        [ObservableProperty] private string _welcomeMessage         = "Good morning, Student!";
        [ObservableProperty] private bool   _isGmailConnected       = false;
        [ObservableProperty] private bool   _isCanvasConnected      = false;
        [ObservableProperty] private bool   _isIcConnected          = false;

        public ObservableCollection<Assignment> RecentMissingAssignments { get; } = new();

        public DashboardViewModel(
            ICanvasService         canvas,
            IInfiniteCampusService ic,
            IGmailService          gmail,
            ILoggingService        log) : base(log)
        {
            _canvas = canvas;
            _ic     = ic;
            _gmail  = gmail;
            UpdateWelcomeMessage();
        }

        [RelayCommand]
        public async Task RefreshDashboardAsync()
        {
            await RunSafeAsync(async () =>
            {
                Log.LogInfo("DashboardViewModel", "Refreshing dashboard data from all sources...");

                // --- Canvas connection + missing count ---
                // IsSessionValidAsync checks whether the cookie session is still live.
                // If not, it triggers LoginAsync automatically inside the service.
                IsCanvasConnected = await _canvas.IsSessionValidAsync();
                if (!IsCanvasConnected)
                {
                    Log.LogInfo("DashboardViewModel",
                        "Canvas session not active. Attempting login...");
                    IsCanvasConnected = await _canvas.LoginAsync();
                }

                if (IsCanvasConnected)
                {
                    var missing = await _canvas.GetMissingAssignmentsAsync();
                    MissingAssignmentCount = missing.Count;
                    RecentMissingAssignments.Clear();
                    foreach (var item in missing.Take(3))
                        RecentMissingAssignments.Add(item);
                    Log.LogInfo("DashboardViewModel",
                        $"Canvas: {missing.Count} missing assignment(s).");
                }

                // --- Infinite Campus (optional, degrades gracefully) ---
                try
                {
                    IsIcConnected = await _ic.IsSessionValidAsync();
                    if (!IsIcConnected)
                        IsIcConnected = await _ic.LoginAsync();

                    if (IsIcConnected)
                        Log.LogInfo("DashboardViewModel", "Infinite Campus: session active.");
                }
                catch (Exception ex)
                {
                    Log.LogWarning("DashboardViewModel",
                        $"Infinite Campus unavailable (non-fatal): {ex.Message}");
                    IsIcConnected = false;
                }

                // --- Gmail authentication status ---
                try
                {
                    IsGmailConnected = await _gmail.IsAuthenticatedAsync();
                }
                catch (Exception ex)
                {
                    Log.LogWarning("DashboardViewModel",
                        $"Gmail status check failed (non-fatal): {ex.Message}");
                    IsGmailConnected = false;
                }

                Log.LogInfo("DashboardViewModel",
                    $"Dashboard refresh complete. " +
                    $"Canvas={IsCanvasConnected} IC={IsIcConnected} Gmail={IsGmailConnected} " +
                    $"Missing={MissingAssignmentCount}");

            }, "Refreshing dashboard...");
        }

        private void UpdateWelcomeMessage()
        {
            int hour = DateTime.Now.Hour;
            WelcomeMessage = hour switch
            {
                < 12 => "Good morning, Student!",
                < 17 => "Good afternoon, Student!",
                _    => "Good evening, Student!"
            };
        }
    }

    // ============================================================
    // GmailViewModel
    // ============================================================

    public partial class GmailViewModel : BaseViewModel
    {
        private readonly IGmailService _gmail;

        [ObservableProperty] private bool        _isAuthenticated = false;
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
                {
                    Log.LogInfo("GmailViewModel", "Gmail authenticated. Loading emails...");
                    await LoadEmailsAsync();
                }
                else
                {
                    Log.LogWarning("GmailViewModel",
                        "Gmail OAuth flow completed but authentication returned false. " +
                        "Check google_credentials.json.");
                }
            }, "Connecting to Gmail...");
        }

        [RelayCommand]
        public async Task LoadEmailsAsync()
        {
            await RunSafeAsync(async () =>
            {
                Log.LogInfo("GmailViewModel", "Fetching school-related emails...");

                // Auto-refresh token if stale before fetching.
                bool stillAuthenticated = await _gmail.IsAuthenticatedAsync();
                if (!stillAuthenticated)
                {
                    Log.LogSessionExpired("GmailViewModel", "Gmail");
                    IsAuthenticated = await _gmail.AuthenticateAsync();
                    if (!IsAuthenticated)
                        throw new ServiceUnavailableException("Gmail",
                            "Gmail session expired and re-authentication failed. " +
                            "Tap Connect Gmail to sign in again.");
                }

                var emails = await _gmail.GetSchoolEmailsAsync(maxResults: 25);
                Emails.Clear();
                foreach (var e in emails) Emails.Add(e);

                Log.LogInfo("GmailViewModel", $"Loaded {Emails.Count} school email(s).");
            }, "Loading emails...");
        }

        [RelayCommand]
        public void DisconnectGmail()
        {
            _gmail.SignOut();
            IsAuthenticated = false;
            Emails.Clear();
            Log.LogInfo("GmailViewModel", "Gmail disconnected by user.");
        }
    }

    // ============================================================
    // AssignmentsViewModel
    // ============================================================

    public partial class AssignmentsViewModel : BaseViewModel
    {
        private readonly ICanvasService          _canvas;
        private readonly IInfiniteCampusService  _ic;
        private readonly IOpenAIService          _openAi;

        [ObservableProperty] private Assignment? _selectedAssignment;
        [ObservableProperty] private string      _statusMessage = string.Empty;

        public ObservableCollection<Assignment> MissingAssignments { get; } = new();
        public event Action<ReviewItem>? ReviewItemReady;

        public AssignmentsViewModel(
            ICanvasService         canvas,
            IInfiniteCampusService ic,
            IOpenAIService         openAi,
            ILoggingService        log) : base(log)
        {
            _canvas = canvas;
            _ic     = ic;
            _openAi = openAi;
        }

        [RelayCommand]
        public async Task LoadMissingAssignmentsAsync()
        {
            await RunSafeAsync(async () =>
            {
                Log.LogInfo("AssignmentsViewModel",
                    "Loading missing assignments from all sources...");
                MissingAssignments.Clear();

                // --- Canvas (primary) ---
                try
                {
                    if (!await _canvas.IsSessionValidAsync())
                    {
                        Log.LogSessionExpired("AssignmentsViewModel", "Canvas");
                        await _canvas.LoginAsync();
                    }
                    var canvasMissing = await _canvas.GetMissingAssignmentsAsync();
                    foreach (var a in canvasMissing)
                        MissingAssignments.Add(a);
                    Log.LogInfo("AssignmentsViewModel",
                        $"Canvas returned {canvasMissing.Count} missing assignment(s).");
                }
                catch (ServiceUnavailableException ex)
                {
                    Log.LogError("AssignmentsViewModel",
                        $"Canvas error while loading: {ex.Message}", ex);
                    StatusMessage = "Canvas unavailable. Showing Infinite Campus data only.";
                }

                // --- Infinite Campus (supplemental, fails gracefully) ---
                try
                {
                    if (!await _ic.IsSessionValidAsync())
                    {
                        Log.LogSessionExpired("AssignmentsViewModel", "InfiniteCampus");
                        await _ic.LoginAsync();
                    }
                    var icMissing = await _ic.GetMissingAssignmentsAsync();
                    foreach (var a in icMissing)
                        MissingAssignments.Add(a);
                    Log.LogInfo("AssignmentsViewModel",
                        $"Infinite Campus returned {icMissing.Count} missing assignment(s).");
                }
                catch (Exception ex)
                {
                    Log.LogWarning("AssignmentsViewModel",
                        $"Infinite Campus unavailable (non-fatal). " +
                        $"Showing Canvas data only. Error: {ex.Message}");
                }

                StatusMessage = $"{MissingAssignments.Count} missing assignment(s) loaded.";
                Log.LogInfo("AssignmentsViewModel",
                    $"Total missing assignments loaded: {MissingAssignments.Count}.");
            }, "Loading missing assignments...");
        }

        [RelayCommand]
        public async Task GenerateAiContentAsync(Assignment assignment)
        {
            if (assignment == null)
            {
                Log.LogWarning("AssignmentsViewModel",
                    "GenerateAiContent called with null assignment.");
                return;
            }

            await RunSafeAsync(async () =>
            {
                Log.LogInfo("AssignmentsViewModel",
                    $"Generating AI content for: '{assignment.Title}' " +
                    $"(ID={assignment.Id}, Course={assignment.CourseName})");
                StatusMessage = $"Generating content for '{assignment.Title}'...";

                var reviewItem = await _openAi.GenerateAssignmentResponseAsync(
                    assignment, new List<string>());

                assignment.HasGeneratedContent = true;
                StatusMessage = "Content generated. Sent to Review tab for your approval.";
                ReviewItemReady?.Invoke(reviewItem);

                Log.LogInfo("AssignmentsViewModel",
                    $"ReviewItem '{reviewItem.ReviewId}' sent to Review tab. " +
                    "Awaiting user approval.");
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
        private readonly HttpClient     _httpClient;

        [ObservableProperty] private string       _inputText         = string.Empty;
        [ObservableProperty] private DataDumpType _selectedInputType = DataDumpType.RawText;
        [ObservableProperty] private string       _statusMessage     = string.Empty;
        [ObservableProperty] private Assignment?  _linkedAssignment;

        public ObservableCollection<DataDumpItem>  DumpItems            { get; } = new();
        public ObservableCollection<Assignment>    AvailableAssignments { get; } = new();
        public event Action<ReviewItem>? ReviewItemReady;

        public DataDumpViewModel(
            ICanvasService canvas,
            IOpenAIService openAi,
            HttpClient     httpClient,
            ILoggingService log) : base(log)
        {
            _canvas     = canvas;
            _openAi     = openAi;
            _httpClient = httpClient;
        }

        [RelayCommand]
        public async Task AddDataItemAsync()
        {
            if (string.IsNullOrWhiteSpace(InputText)) return;

            await RunSafeAsync(async () =>
            {
                string rawContent = InputText.Trim();
                string resolved   = rawContent;

                // For URLs, fetch the page content to use as context.
                if (SelectedInputType == DataDumpType.GoogleDocsUrl
                 || SelectedInputType == DataDumpType.PastedLink)
                {
                    Log.LogInfo("DataDumpViewModel",
                        $"Fetching URL content: {rawContent}");
                    resolved = await FetchLinkContentAsync(rawContent);
                }

                var item = new DataDumpItem
                {
                    RawContent         = rawContent,
                    ResolvedContent    = resolved,
                    InputType          = SelectedInputType,
                    LinkedAssignmentId = LinkedAssignment?.Id,
                    IsProcessed        = true
                };

                DumpItems.Add(item);
                InputText     = string.Empty;
                StatusMessage = $"Data item added. Total: {DumpItems.Count} item(s).";

                Log.LogInfo("DataDumpViewModel",
                    $"Added data dump item type={item.InputType}. " +
                    $"Resolved length: {resolved.Length} chars.");
            }, "Processing input...");
        }

        [RelayCommand]
        public void RemoveItem(DataDumpItem item)
        {
            DumpItems.Remove(item);
            StatusMessage = $"{DumpItems.Count} item(s) in data dump.";
            Log.LogInfo("DataDumpViewModel", $"Removed data dump item '{item.ItemId}'.");
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
                var supplemental = DumpItems
                    .Where(i => i.IsProcessed)
                    .Select(i => i.ResolvedContent ?? i.RawContent)
                    .ToList();

                Log.LogInfo("DataDumpViewModel",
                    $"Generating for '{LinkedAssignment.Title}' with " +
                    $"{supplemental.Count} supplemental item(s).");

                var reviewItem = await _openAi.GenerateAssignmentResponseAsync(
                    LinkedAssignment, supplemental);

                StatusMessage = "Content generated with your data. Check the Review tab.";
                ReviewItemReady?.Invoke(reviewItem);

                Log.LogInfo("DataDumpViewModel",
                    $"ReviewItem '{reviewItem.ReviewId}' sent to Review. " +
                    "Awaiting user approval before any submission.");
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
                Log.LogInfo("DataDumpViewModel",
                    $"Loaded {AvailableAssignments.Count} assignments into picker.");
            }, "Loading assignments...");
        }

        private async Task<string> FetchLinkContentAsync(string url)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                HttpResponseMessage resp = await _httpClient.GetAsync(url);
                sw.Stop();
                Log.LogApiCall("DataDumpViewModel", "GET", url,
                    (int)resp.StatusCode, sw.ElapsedMilliseconds);

                if (!resp.IsSuccessStatusCode)
                {
                    Log.LogWarning("DataDumpViewModel",
                        $"URL fetch returned HTTP {(int)resp.StatusCode} for: {url}");
                    return $"[Could not retrieve content from: {url} — HTTP {(int)resp.StatusCode}]";
                }

                string content = await resp.Content.ReadAsStringAsync();
                string truncated = content.Length > 5000
                    ? content[..5000] + "\n... [content truncated at 5000 chars]"
                    : content;

                Log.LogInfo("DataDumpViewModel",
                    $"Fetched {content.Length} chars from URL. " +
                    $"Truncated to {truncated.Length} chars for prompt.");
                return truncated;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Log.LogError("DataDumpViewModel", $"Failed to fetch URL: {url}", ex);
                return $"[Error fetching URL: {url} — {ex.Message}]";
            }
        }
    }

    // ============================================================
    // ReviewViewModel
    // CRITICAL SAFETY NOTE:
    //   No submission can ever occur without the user explicitly tapping Approve.
    //   ApproveContent() is the ONLY place IsApprovedByUser is set to true.
    //   CanvasService.SubmitAssignmentAsync has a SECOND independent guard:
    //   it throws UnauthorizedSubmissionException if IsApprovedByUser is false
    //   regardless of how SubmitToCanvasAsync was called.
    // ============================================================

    public partial class ReviewViewModel : BaseViewModel
    {
        private readonly ICanvasService _canvas;

        [ObservableProperty] private ReviewItem? _selectedReviewItem;
        [ObservableProperty] private string      _statusMessage = string.Empty;
        [ObservableProperty] private bool        _canSubmit     = false;

        public ObservableCollection<ReviewItem> PendingReviewItems { get; } = new();

        public ReviewViewModel(ICanvasService canvas, ILoggingService log) : base(log)
        {
            _canvas = canvas;
        }

        public void AddReviewItem(ReviewItem item)
        {
            PendingReviewItems.Add(item);
            SelectedReviewItem = item;
            CanSubmit          = false; // Always reset: new items are never pre-approved.
            StatusMessage      = $"{PendingReviewItems.Count} item(s) awaiting your review.";

            Log.LogInfo("ReviewViewModel",
                $"ReviewItem '{item.ReviewId}' added for '{item.SourceAssignment.Title}'. " +
                $"IsApprovedByUser=FALSE. User must explicitly tap Approve.");
        }

        [RelayCommand]
        public void UpdateEditedContent(string content)
        {
            if (SelectedReviewItem == null) return;
            SelectedReviewItem.EditedContent = content;
            // Editing resets approval — user must re-approve after any edit.
            if (SelectedReviewItem.IsApprovedByUser)
            {
                SelectedReviewItem.IsApprovedByUser = false;
                SelectedReviewItem.Status           = ReviewItemStatus.PendingReview;
                CanSubmit = false;
                Log.LogInfo("ReviewViewModel",
                    $"ReviewItem '{SelectedReviewItem.ReviewId}' edited. " +
                    "Approval RESET. User must re-approve.");
            }
        }

        [RelayCommand]
        public void ApproveContent()
        {
            if (SelectedReviewItem == null)
            {
                Log.LogWarning("ReviewViewModel", "ApproveContent called with no item selected.");
                return;
            }

            // THE ONLY PLACE in the entire codebase where IsApprovedByUser is set true.
            SelectedReviewItem.IsApprovedByUser = true;
            SelectedReviewItem.ApprovedAt       = DateTimeOffset.UtcNow;
            SelectedReviewItem.Status           = ReviewItemStatus.Approved;
            CanSubmit = true;

            Log.LogInfo("ReviewViewModel",
                $"USER APPROVED ReviewItem '{SelectedReviewItem.ReviewId}' " +
                $"for '{SelectedReviewItem.SourceAssignment.Title}' " +
                $"at {SelectedReviewItem.ApprovedAt:yyyy-MM-dd HH:mm:ss}. " +
                "Submit button is now enabled.");

            StatusMessage = "Content approved! You may now submit to Canvas.";
        }

        [RelayCommand]
        public void RejectContent()
        {
            if (SelectedReviewItem == null) return;

            SelectedReviewItem.IsApprovedByUser = false;
            SelectedReviewItem.Status           = ReviewItemStatus.Rejected;
            CanSubmit = false;

            Log.LogInfo("ReviewViewModel",
                $"USER REJECTED ReviewItem '{SelectedReviewItem.ReviewId}'. " +
                "Submission blocked. User should return to Assignments to regenerate.");

            StatusMessage = "Content rejected. Return to Assignments to regenerate.";
        }

        [RelayCommand]
        public async Task SubmitToCanvasAsync()
        {
            // UI-layer guard: should not be reachable due to CanSubmit binding,
            // but we check explicitly as a defence-in-depth layer.
            if (SelectedReviewItem == null)
            {
                ShowError("No review item selected.");
                return;
            }

            if (!SelectedReviewItem.IsApprovedByUser)
            {
                string msg =
                    $"BLOCKED: Attempt to submit ReviewItem " +
                    $"'{SelectedReviewItem.ReviewId}' without approval.";
                Log.LogSubmissionBlocked("ReviewViewModel",
                    SelectedReviewItem.SourceAssignment.Id, msg);
                ShowError("You must tap Approve before submitting. " +
                          "This assignment has not been approved.");
                return;
            }

            await RunSafeAsync(async () =>
            {
                Log.LogInfo("ReviewViewModel",
                    $"Submitting ReviewItem '{SelectedReviewItem.ReviewId}' " +
                    $"for '{SelectedReviewItem.SourceAssignment.Title}' to Canvas. " +
                    $"ApprovedAt={SelectedReviewItem.ApprovedAt:HH:mm:ss}");

                StatusMessage = "Submitting to Canvas...";

                // CanvasService will ALSO independently verify IsApprovedByUser
                // and throw UnauthorizedSubmissionException if false.
                bool success = await _canvas.SubmitAssignmentAsync(SelectedReviewItem);

                if (success)
                {
                    SelectedReviewItem.Status      = ReviewItemStatus.Submitted;
                    SelectedReviewItem.SubmittedAt = DateTimeOffset.UtcNow;

                    Log.LogInfo("ReviewViewModel",
                        $"Assignment '{SelectedReviewItem.SourceAssignment.Title}' " +
                        $"submitted successfully at {SelectedReviewItem.SubmittedAt:HH:mm:ss}.");

                    PendingReviewItems.Remove(SelectedReviewItem);
                    SelectedReviewItem = PendingReviewItems.Count > 0
                        ? PendingReviewItems[0] : null;
                    CanSubmit     = SelectedReviewItem?.IsApprovedByUser ?? false;
                    StatusMessage = "Assignment submitted successfully to Canvas!";
                }
                else
                {
                    SelectedReviewItem.Status = ReviewItemStatus.SubmissionFailed;
                    StatusMessage = "Submission failed. Check your Canvas connection.";
                    Log.LogError("ReviewViewModel",
                        $"Canvas submission returned false for " +
                        $"'{SelectedReviewItem.SourceAssignment.Title}'. " +
                        "Check the log file for full details.");
                }
            }, "Submitting to Canvas...");
        }

        [RelayCommand]
        public void SelectReviewItem(ReviewItem item)
        {
            SelectedReviewItem = item;
            CanSubmit          = item.IsApprovedByUser;
            Log.LogDebug("ReviewViewModel",
                $"Selected ReviewItem '{item.ReviewId}'. " +
                $"IsApprovedByUser={item.IsApprovedByUser}. CanSubmit={CanSubmit}.");
        }
    }
}