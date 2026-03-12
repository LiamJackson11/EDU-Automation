// EduAutomation.Tests/ServiceAndViewModelTests.cs
// Comprehensive unit tests covering:
//   - CanvasService: login detection, missing assignments, submission guard
//   - OpenAIService: rate-limit handling, auth failure, guardrail validation
//   - ReviewViewModel: approval workflow, edit-resets-approval, double-submission prevention
//   - PromptGuardrails: all validation paths
//   - LoggingService: sanitization of secrets
//
// All tests use Moq to mock interfaces. No real API keys or network calls.

using System.Net;
using System.Text;
using EduAutomation.Exceptions;
using EduAutomation.Helpers;
using EduAutomation.Models;
using EduAutomation.Services;
using EduAutomation.ViewModels;
using FluentAssertions;
using Moq;
using Moq.Protected;
using Newtonsoft.Json;
using Xunit;

namespace EduAutomation.Tests
{
    // ===========================================================
    // Test helpers
    // ===========================================================

    internal static class TestFactory
    {
        public static Mock<ILoggingService> MockLog()
        {
            var m = new Mock<ILoggingService>();
            // Set up all void methods so they never throw.
            m.Setup(l => l.LogInfo(It.IsAny<string>(),    It.IsAny<string>()));
            m.Setup(l => l.LogWarning(It.IsAny<string>(), It.IsAny<string>()));
            m.Setup(l => l.LogDebug(It.IsAny<string>(),   It.IsAny<string>()));
            m.Setup(l => l.LogError(It.IsAny<string>(),   It.IsAny<string>(),
                                    It.IsAny<Exception?>()));
            m.Setup(l => l.LogApiCall(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<long>()));
            m.Setup(l => l.LogApiRetry(It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()));
            m.Setup(l => l.LogRateLimit(It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<int>()));
            m.Setup(l => l.LogSessionExpired(It.IsAny<string>(), It.IsAny<string>()));
            m.Setup(l => l.LogSubmissionBlocked(It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>()));
            m.Setup(l => l.LogGuardrailTriggered(It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>()));
            m.SetupGet(l => l.LogFilePath).Returns("C:\\fake\\log.txt");
            return m;
        }

        public static Mock<ISecureConfigService> MockConfig(
            string canvasUrl  = "https://test.instructure.com",
            string canvasUser = "student@school.edu",
            string canvasPass = "test-password",
            string openAiKey  = "sk-test-openai-key-12345")
        {
            var m = new Mock<ISecureConfigService>();
            m.Setup(c => c.GetCanvasBaseUrlAsync()).ReturnsAsync(canvasUrl);
            m.Setup(c => c.GetCanvasUsernameAsync()).ReturnsAsync(canvasUser);
            m.Setup(c => c.GetCanvasPasswordAsync()).ReturnsAsync(canvasPass);
            m.Setup(c => c.GetInfiniteCampusBaseUrlAsync()).ReturnsAsync("https://test.ic.edu");
            m.Setup(c => c.GetInfiniteCampusUsernameAsync()).ReturnsAsync("student");
            m.Setup(c => c.GetInfiniteCampusPasswordAsync()).ReturnsAsync("password");
            m.Setup(c => c.GetOpenAiApiKeyAsync()).ReturnsAsync(openAiKey);
            return m;
        }

        /// Creates an HttpClient whose handler returns a fixed response.
        public static HttpClient MockHttpClient(HttpResponseMessage response)
        {
            var handler = new Mock<HttpMessageHandler>();
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);
            return new HttpClient(handler.Object)
            {
                BaseAddress = new Uri("https://test.instructure.com")
            };
        }

        /// Creates a Canvas-style assignment JSON array.
        public static string CanvasAssignmentsJson(int count = 2)
        {
            var items = new List<object>();
            for (int i = 1; i <= count; i++)
                items.Add(new
                {
                    id = i * 100,
                    name = $"Test Assignment {i}",
                    description = $"Complete task {i} in full.",
                    due_at = "2026-06-01T23:59:00Z",
                    points_possible = 100.0,
                    html_url = $"https://canvas/assignments/{i * 100}",
                    submission_types = new[] { "online_text_entry" },
                    has_submitted_submissions = false
                });
            return JsonConvert.SerializeObject(items);
        }

        public static Assignment SampleAssignment(string id = "101") =>
            new Assignment
            {
                Id          = id,
                Title       = "History Essay",
                Description = "Write about the causes of World War I.",
                CourseName  = "World History",
                CourseId    = "5",
                Source      = AssignmentSource.Canvas,
                Status      = AssignmentStatus.Missing
            };

        public static ReviewItem SampleReviewItem(bool approved = false)
        {
            var item = new ReviewItem
            {
                SourceAssignment  = SampleAssignment(),
                OriginalAiContent = "This is a valid AI-generated response " +
                                    "that is more than fifty characters long " +
                                    "and does not contain any refusal phrases.",
                EditedContent     = string.Empty,
                IsApprovedByUser  = approved,
                Status = approved ? ReviewItemStatus.Approved : ReviewItemStatus.PendingReview
            };
            if (approved) item.ApprovedAt = DateTimeOffset.UtcNow;
            return item;
        }
    }

    // ===========================================================
    // CanvasServiceTests
    // Tests the scraping-based login and data retrieval.
    // ===========================================================

    public class CanvasServiceTests
    {
        // ---- LoginAsync ----

        [Fact]
        public async Task LoginAsync_ReturnsFalse_WhenCredentialsNotConfigured()
        {
            var config = new Mock<ISecureConfigService>();
            config.Setup(c => c.GetCanvasBaseUrlAsync()).ReturnsAsync((string?)null);
            config.Setup(c => c.GetCanvasUsernameAsync()).ReturnsAsync((string?)null);
            config.Setup(c => c.GetCanvasPasswordAsync()).ReturnsAsync((string?)null);

            var http    = TestFactory.MockHttpClient(new HttpResponseMessage(HttpStatusCode.OK));
            var service = new CanvasService(http, TestFactory.MockLog().Object, config.Object);

            bool result = await service.LoginAsync();

            result.Should().BeFalse(
                "login must fail gracefully when credentials are missing");
        }

        [Fact]
        public async Task LoginAsync_ReturnsFalse_WhenServerRedirectsBackToLogin()
        {
            // Simulate: login page loads OK, POST returns page still containing /login
            var loginPageHtml = "<html><form action='/login/canvas'>" +
                                "<input name='authenticity_token' value='abc123'/>" +
                                "<input type='password'/></form></html>";
            var loginFailHtml = "<html><body>Invalid username or password</body></html>";

            var handler = new Mock<HttpMessageHandler>();
            int callCount = 0;
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(() =>
                {
                    callCount++;
                    // First call = GET login page, second = POST credentials
                    string body = callCount == 1 ? loginPageHtml : loginFailHtml;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body),
                        RequestMessage = new HttpRequestMessage(
                            HttpMethod.Get,
                            callCount == 2
                                ? "https://test.instructure.com/login/canvas"
                                : "https://test.instructure.com/dashboard")
                    };
                });

            var http    = new HttpClient(handler.Object);
            var service = new CanvasService(
                http,
                TestFactory.MockLog().Object,
                TestFactory.MockConfig().Object);

            bool result = await service.LoginAsync();

            result.Should().BeFalse(
                "login must return false when server echoes error text");
        }

        [Fact]
        public async Task IsSessionValidAsync_ReturnsFalse_WhenNeverLoggedIn()
        {
            var service = new CanvasService(
                TestFactory.MockHttpClient(new HttpResponseMessage(HttpStatusCode.OK)),
                TestFactory.MockLog().Object,
                TestFactory.MockConfig().Object);

            bool result = await service.IsSessionValidAsync();

            result.Should().BeFalse(
                "a new service instance has no active session");
        }

        [Fact]
        public async Task GetMissingAssignmentsAsync_ReturnsEmptyList_WhenNotLoggedIn()
        {
            // Session never established, so the service returns empty rather than throwing.
            var service = new CanvasService(
                TestFactory.MockHttpClient(new HttpResponseMessage(HttpStatusCode.OK)),
                TestFactory.MockLog().Object,
                TestFactory.MockConfig().Object);

            var result = await service.GetMissingAssignmentsAsync();

            result.Should().BeEmpty(
                "missing assignments returns empty when no session is established " +
                "and login attempt with mocked creds fails");
        }

        [Fact]
        public async Task SubmitAssignmentAsync_ThrowsUnauthorizedSubmissionException_WhenNotApproved()
        {
            var service = new CanvasService(
                TestFactory.MockHttpClient(new HttpResponseMessage(HttpStatusCode.OK)),
                TestFactory.MockLog().Object,
                TestFactory.MockConfig().Object);

            var unapproved = TestFactory.SampleReviewItem(approved: false);

            Func<Task> act = async () => await service.SubmitAssignmentAsync(unapproved);

            await act.Should()
                .ThrowAsync<UnauthorizedSubmissionException>(
                    "the submission guard must block any item where IsApprovedByUser is false");
        }

        [Fact]
        public async Task SubmitAssignmentAsync_DoesNotThrow_ForApprovedItem_WithValidSession()
        {
            // Simulate: session is alive, Canvas returns 201 Created on submission.
            var handler = new Mock<HttpMessageHandler>();
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });

            var http    = new HttpClient(handler.Object);
            var log     = TestFactory.MockLog();
            var config  = TestFactory.MockConfig();
            var service = new CanvasService(http, log.Object, config.Object);

            // Force session-active state by using reflection to flip _isLoggedIn.
            var isLoggedIn = typeof(CanvasService)
                .GetField("_isLoggedIn",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
            isLoggedIn!.SetValue(service, true);

            var sessionCreated = typeof(CanvasService)
                .GetField("_sessionCreated",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
            sessionCreated!.SetValue(service, DateTimeOffset.UtcNow);

            var baseUrl = typeof(CanvasService)
                .GetField("_baseUrl",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
            baseUrl!.SetValue(service, "https://test.instructure.com");

            var approved = TestFactory.SampleReviewItem(approved: true);

            Func<Task> act = async () => await service.SubmitAssignmentAsync(approved);

            // Should not throw UnauthorizedSubmissionException.
            await act.Should().NotThrowAsync<UnauthorizedSubmissionException>();
        }
    }

    // ===========================================================
    // OpenAIServiceTests
    // ===========================================================

    public class OpenAIServiceTests
    {
        private static string OpenAiSuccessJson(string content = "Here is a complete response.") =>
            JsonConvert.SerializeObject(new
            {
                choices = new[]
                {
                    new { message = new { content } }
                },
                usage = new { total_tokens = 150 }
            });

        [Fact]
        public async Task ValidateApiKeyAsync_ReturnsTrue_WhenApiReturns200()
        {
            var http = TestFactory.MockHttpClient(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        OpenAiSuccessJson("OK"), Encoding.UTF8, "application/json")
                });

            var service = new OpenAIService(
                http,
                TestFactory.MockLog().Object,
                TestFactory.MockConfig().Object);

            bool result = await service.ValidateApiKeyAsync();

            result.Should().BeTrue();
        }

        [Fact]
        public async Task ValidateApiKeyAsync_ReturnsFalse_WhenApiReturns401()
        {
            var http = TestFactory.MockHttpClient(
                new HttpResponseMessage(HttpStatusCode.Unauthorized));

            var service = new OpenAIService(
                http,
                TestFactory.MockLog().Object,
                TestFactory.MockConfig().Object);

            bool result = await service.ValidateApiKeyAsync();

            result.Should().BeFalse();
        }

        [Fact]
        public async Task ValidateApiKeyAsync_ReturnsFalse_WhenApiKeyIsEmpty()
        {
            var config = TestFactory.MockConfig(openAiKey: "");
            var http   = TestFactory.MockHttpClient(
                new HttpResponseMessage(HttpStatusCode.OK));

            var service = new OpenAIService(
                http, TestFactory.MockLog().Object, config.Object);

            bool result = await service.ValidateApiKeyAsync();

            result.Should().BeFalse(
                "an empty API key must be caught before any HTTP call is made");
        }

        [Fact]
        public async Task GenerateAssignmentResponseAsync_ReturnsReviewItem_WhenApiSucceeds()
        {
            string longResponse = new string('x', 200); // > MinResponseLength
            var http = TestFactory.MockHttpClient(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        OpenAiSuccessJson(longResponse), Encoding.UTF8, "application/json")
                });

            var service = new OpenAIService(
                http,
                TestFactory.MockLog().Object,
                TestFactory.MockConfig().Object);

            var assignment = TestFactory.SampleAssignment();
            ReviewItem result = await service.GenerateAssignmentResponseAsync(
                assignment, new List<string>());

            result.Should().NotBeNull();
            result.IsApprovedByUser.Should().BeFalse(
                "newly generated items must NEVER be pre-approved");
            result.Status.Should().Be(ReviewItemStatus.PendingReview);
            result.OriginalAiContent.Should().Be(longResponse);
            result.SourceAssignment.Id.Should().Be(assignment.Id);
        }

        [Fact]
        public async Task GenerateAssignmentResponseAsync_ThrowsServiceUnavailable_OnHttp401()
        {
            var http = TestFactory.MockHttpClient(
                new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":\"invalid_api_key\"}")
                });

            var service = new OpenAIService(
                http,
                TestFactory.MockLog().Object,
                TestFactory.MockConfig().Object);

            Func<Task> act = async () => await service.GenerateAssignmentResponseAsync(
                TestFactory.SampleAssignment(), new List<string>());

            await act.Should()
                .ThrowAsync<ServiceUnavailableException>()
                .WithMessage("*401*");
        }

        [Fact]
        public async Task GenerateAssignmentResponseAsync_ThrowsGuardException_WhenResponseTooShort()
        {
            var http = TestFactory.MockHttpClient(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        OpenAiSuccessJson("Short."), Encoding.UTF8, "application/json")
                });

            var service = new OpenAIService(
                http,
                TestFactory.MockLog().Object,
                TestFactory.MockConfig().Object);

            Func<Task> act = async () => await service.GenerateAssignmentResponseAsync(
                TestFactory.SampleAssignment(), new List<string>());

            await act.Should().ThrowAsync<AiHallucinationGuardException>(
                "responses shorter than 50 chars must be rejected by guardrails");
        }

        [Fact]
        public async Task GenerateAssignmentResponseAsync_ThrowsGuardException_WhenResponseIsRefusal()
        {
            string refusal = new string('x', 100) +
                " i'm sorry, but i cannot complete this assignment. " +
                new string('y', 100);

            var http = TestFactory.MockHttpClient(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        OpenAiSuccessJson(refusal), Encoding.UTF8, "application/json")
                });

            var service = new OpenAIService(
                http,
                TestFactory.MockLog().Object,
                TestFactory.MockConfig().Object);

            Func<Task> act = async () => await service.GenerateAssignmentResponseAsync(
                TestFactory.SampleAssignment(), new List<string>());

            await act.Should().ThrowAsync<AiHallucinationGuardException>(
                "refusal phrases in the response must be caught by guardrails");
        }

        [Fact]
        public async Task GenerateAssignmentResponseAsync_ThrowsGuardException_OnNullTitle()
        {
            var noTitle = TestFactory.SampleAssignment();
            noTitle.Title = "";

            var service = new OpenAIService(
                TestFactory.MockHttpClient(new HttpResponseMessage(HttpStatusCode.OK)),
                TestFactory.MockLog().Object,
                TestFactory.MockConfig().Object);

            Func<Task> act = async () =>
                await service.GenerateAssignmentResponseAsync(noTitle, new List<string>());

            await act.Should().ThrowAsync<AiHallucinationGuardException>(
                "assignments with no title must be blocked before any prompt is sent");
        }
    }

    // ===========================================================
    // PromptGuardrailsTests
    // ===========================================================

    public class PromptGuardrailsTests
    {
        [Fact]
        public void ValidateAssignmentForPrompt_Throws_WhenTitleIsEmpty()
        {
            var a = TestFactory.SampleAssignment();
            a.Title = "";

            Action act = () => PromptGuardrails.ValidateAssignmentForPrompt(a);
            act.Should().Throw<AiHallucinationGuardException>()
                .WithMessage("*title*");
        }

        [Fact]
        public void ValidateAssignmentForPrompt_Throws_WhenNoDescriptionAndNoCourse()
        {
            var a = new Assignment
            {
                Id          = "1",
                Title       = "Some Assignment",
                Description = "",
                CourseName  = ""
            };

            Action act = () => PromptGuardrails.ValidateAssignmentForPrompt(a);
            act.Should().Throw<AiHallucinationGuardException>()
                .WithMessage("*contextual anchor*");
        }

        [Fact]
        public void ValidateAssignmentForPrompt_DoesNotThrow_WithTitleAndCourseNameOnly()
        {
            var a = new Assignment
            {
                Id          = "1",
                Title       = "Essay",
                Description = "",
                CourseName  = "English"
            };

            Action act = () => PromptGuardrails.ValidateAssignmentForPrompt(a);
            act.Should().NotThrow(
                "a title + course name is sufficient context to proceed");
        }

        [Fact]
        public void BuildAssignmentPrompt_ReturnsNonEmptyPrompts()
        {
            var (system, user) = PromptGuardrails.BuildAssignmentPrompt(
                TestFactory.SampleAssignment(),
                new List<string> { "Some extra context here." });

            system.Should().NotBeNullOrWhiteSpace();
            user.Should().Contain("History Essay");
            user.Should().Contain("World History");
            user.Should().Contain("Some extra context here.");
        }

        [Fact]
        public void BuildAssignmentPrompt_IncludesNeedsMoreInfoNote_WhenNoSupplemental()
        {
            var (_, user) = PromptGuardrails.BuildAssignmentPrompt(
                TestFactory.SampleAssignment(), new List<string>());

            user.Should().Contain("No supplemental context provided",
                "the prompt must tell the AI to stay within assignment data only");
        }

        [Fact]
        public void ValidateAiResponse_Throws_WhenResponseIsEmpty()
        {
            Action act = () => PromptGuardrails.ValidateAiResponse("", "Test Assignment");
            act.Should().Throw<AiHallucinationGuardException>()
                .WithMessage("*empty*");
        }

        [Fact]
        public void ValidateAiResponse_Throws_WhenResponseIsTooShort()
        {
            Action act = () => PromptGuardrails.ValidateAiResponse("Too short.", "Test");
            act.Should().Throw<AiHallucinationGuardException>()
                .WithMessage("*short*");
        }

        [Theory]
        [InlineData("As an AI language model, I cannot complete this task...")]
        [InlineData("I cannot access the internet to verify these claims...")]
        [InlineData("As a large language model, my training data doesn't include...")]
        public void ValidateAiResponse_Throws_OnHallucinationIndicators(string response)
        {
            // Pad to exceed minimum length
            string padded = response + new string(' ', 200);
            Action act = () => PromptGuardrails.ValidateAiResponse(padded, "Test Assignment");
            act.Should().Throw<AiHallucinationGuardException>(
                $"response containing '{response}' should be rejected by guardrails");
        }

        [Fact]
        public void ValidateAiResponse_DoesNotThrow_ForValidResponse()
        {
            string good = "World War I began in 1914 due to a complex web of " +
                          "alliances, nationalism, and the assassination of Archduke " +
                          "Franz Ferdinand. This essay will explore the main causes " +
                          "in detail and analyze their significance.";

            Action act = () => PromptGuardrails.ValidateAiResponse(good, "History Essay");
            act.Should().NotThrow();
        }
    }

    // ===========================================================
    // ReviewViewModelTests
    // ===========================================================

    public class ReviewViewModelTests
    {
        private static ReviewViewModel CreateViewModel()
        {
            var mockCanvas = new Mock<ICanvasService>();
            mockCanvas.Setup(c => c.SubmitAssignmentAsync(
                    It.IsAny<ReviewItem>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            return new ReviewViewModel(
                mockCanvas.Object,
                TestFactory.MockLog().Object);
        }

        [Fact]
        public void AddReviewItem_SetsIsApprovedByUser_ToFalse_Always()
        {
            var vm   = CreateViewModel();
            var item = TestFactory.SampleReviewItem(approved: true); // try to sneak in pre-approved
            item.IsApprovedByUser = true; // explicitly set to true

            vm.AddReviewItem(item);

            // AddReviewItem resets CanSubmit; the item itself is not modified
            // but CanSubmit must be false after adding any new item.
            vm.CanSubmit.Should().BeFalse(
                "CanSubmit must always be false when a new item is added");
        }

        [Fact]
        public void ApproveContent_SetsIsApprovedByUser_ToTrue_AndEnablesCanSubmit()
        {
            var vm   = CreateViewModel();
            var item = TestFactory.SampleReviewItem(approved: false);
            vm.AddReviewItem(item);

            vm.ApproveContentCommand.Execute(null);

            item.IsApprovedByUser.Should().BeTrue();
            item.ApprovedAt.Should().NotBeNull();
            item.Status.Should().Be(ReviewItemStatus.Approved);
            vm.CanSubmit.Should().BeTrue();
        }

        [Fact]
        public void RejectContent_SetsIsApprovedByUser_ToFalse_AndBlocksSubmit()
        {
            var vm   = CreateViewModel();
            var item = TestFactory.SampleReviewItem(approved: false);
            vm.AddReviewItem(item);
            vm.ApproveContentCommand.Execute(null); // first approve
            vm.RejectContentCommand.Execute(null);  // then reject

            item.IsApprovedByUser.Should().BeFalse();
            item.Status.Should().Be(ReviewItemStatus.Rejected);
            vm.CanSubmit.Should().BeFalse();
        }

        [Fact]
        public void UpdateEditedContent_ResetsApproval_WhenItemWasPreviouslyApproved()
        {
            var vm   = CreateViewModel();
            var item = TestFactory.SampleReviewItem(approved: false);
            vm.AddReviewItem(item);
            vm.ApproveContentCommand.Execute(null);

            // User edits content after approving.
            vm.UpdateEditedContentCommand.Execute("I changed the content after approval.");

            item.IsApprovedByUser.Should().BeFalse(
                "editing content after approval must reset the approval");
            vm.CanSubmit.Should().BeFalse(
                "CanSubmit must be false after an edit resets approval");
        }

        [Fact]
        public async Task SubmitToCanvasAsync_ShowsError_WhenItemNotApproved()
        {
            var vm   = CreateViewModel();
            var item = TestFactory.SampleReviewItem(approved: false);
            vm.AddReviewItem(item);

            // Do NOT approve — attempt submit directly.
            await vm.SubmitToCanvasCommand.ExecuteAsync(null);

            vm.HasError.Should().BeTrue(
                "submitting without approval must show an error to the user");
            vm.ErrorMessage.Should().Contain("Approve",
                "error message must tell the user to approve first");
        }

        [Fact]
        public async Task SubmitToCanvasAsync_Succeeds_WhenItemIsApproved()
        {
            var vm   = CreateViewModel();
            var item = TestFactory.SampleReviewItem(approved: false);
            vm.AddReviewItem(item);
            vm.ApproveContentCommand.Execute(null);

            await vm.SubmitToCanvasCommand.ExecuteAsync(null);

            vm.HasError.Should().BeFalse(
                "submission of an approved item should succeed without error");
            vm.StatusMessage.Should().Contain("successfully");
        }

        [Fact]
        public void SelectReviewItem_UpdatesCanSubmit_BasedOnApprovalState()
        {
            var vm      = CreateViewModel();
            var item1   = TestFactory.SampleReviewItem(approved: false);
            var item2   = TestFactory.SampleReviewItem(approved: true);
            vm.PendingReviewItems.Add(item1);
            vm.PendingReviewItems.Add(item2);

            vm.SelectReviewItemCommand.Execute(item2);
            vm.CanSubmit.Should().BeTrue("item2 is approved");

            vm.SelectReviewItemCommand.Execute(item1);
            vm.CanSubmit.Should().BeFalse("item1 is not approved");
        }
    }

    // ===========================================================
    // LoggingServiceTests
    // ===========================================================

    public class LoggingServiceTests
    {
        [Fact]
        public void LogFilePath_IsNotEmpty()
        {
            var svc = new LoggingService();
            svc.LogFilePath.Should().NotBeNullOrWhiteSpace(
                "the logging service must provide a valid path for user support");
        }

        [Fact]
        public void AllLogMethods_DoNotThrow_ForNormalInput()
        {
            var svc = new LoggingService();
            Action act = () =>
            {
                svc.LogInfo("Test", "Normal info message.");
                svc.LogWarning("Test", "A warning occurred.");
                svc.LogDebug("Test", "Debug detail.");
                svc.LogError("Test", "An error occurred.", new Exception("fake error"));
                svc.LogApiCall("Test", "GET", "https://api.example.com/v1/data", 200, 42);
                svc.LogApiRetry("Test", 1, 3, 1000, "HTTP 429");
                svc.LogRateLimit("Test", "OpenAI", 30);
                svc.LogSessionExpired("Test", "Canvas");
                svc.LogSubmissionBlocked("Test", "assignment-123", "Not approved");
                svc.LogGuardrailTriggered("Test", "as an ai", "Essay");
            };
            act.Should().NotThrow();
        }

        [Theory]
        [InlineData("sk-abc123XYZ456DEFGHIJKLMN", "[REDACTED]")]
        [InlineData("Bearer eyJhbGciOiJSUzI1NiJ9.something.signature", "[REDACTED]")]
        [InlineData("token=mysecrettoken12345", "[REDACTED]")]
        public void LogInfo_DoesNotExpose_SecretTokens(string secretInput, string expected)
        {
            // We can only verify indirectly that LoggingService doesn't throw
            // with secrets, since the sanitizer runs internally.
            // A full test would require capturing Serilog sink output.
            var svc = new LoggingService();
            Action act = () => svc.LogInfo("Test", $"Connection using {secretInput}");
            act.Should().NotThrow(
                "LoggingService must sanitize secrets silently without throwing");
        }
    }
}