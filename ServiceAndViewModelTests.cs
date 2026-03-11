// EduAutomation.Tests/ServiceAndViewModelTests.cs
// Contains: CanvasServiceTests, OpenAIServiceTests, ReviewViewModelTests
// All using directives are at the top of the file.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EduAutomation.Exceptions;
using EduAutomation.Models;
using EduAutomation.Services;
using EduAutomation.ViewModels;
using FluentAssertions;
using Moq;
using Moq.Protected;
using Newtonsoft.Json;
using Xunit;

namespace EduAutomation.Tests.Services
{
    // ============================================================
    // CanvasServiceTests
    // ============================================================

    public class CanvasServiceTests
    {
        private readonly Mock<ILoggingService> _mockLog;
        private readonly Mock<ISecureConfigService> _mockConfig;

        public CanvasServiceTests()
        {
            _mockLog = new Mock<ILoggingService>();
            _mockConfig = new Mock<ISecureConfigService>();
            _mockConfig.Setup(c => c.GetCanvasApiTokenAsync())
                .ReturnsAsync("test-canvas-token-12345");
            _mockConfig.Setup(c => c.GetCanvasBaseUrlAsync())
                .ReturnsAsync("https://test.instructure.com");
        }

        private CanvasService CreateService(HttpResponseMessage response)
        {
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(response);
            var httpClient = new HttpClient(handlerMock.Object);
            return new CanvasService(httpClient, _mockLog.Object, _mockConfig.Object);
        }

        [Fact]
        public async Task ValidateTokenAsync_ReturnsTrue_WhenApiReturns200()
        {
            var service = CreateService(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":1,\"name\":\"Test Student\"}")
            });
            bool result = await service.ValidateTokenAsync();
            result.Should().BeTrue();
        }

        [Fact]
        public async Task ValidateTokenAsync_ReturnsFalse_WhenApiReturns401()
        {
            var service = CreateService(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            bool result = await service.ValidateTokenAsync();
            result.Should().BeFalse();
        }

        [Fact]
        public async Task GetMissingAssignmentsAsync_ReturnsAssignments_WhenApiSucceeds()
        {
            var assignments = new[]
            {
                new
                {
                    id = 101, name = "History Essay",
                    description = "Write about WW2",
                    due_at = "2025-12-01T23:59:00Z",
                    points_possible = 100.0,
                    html_url = "https://canvas/assignments/101",
                    submission_types = new[] { "online_text_entry" }
                },
                new
                {
                    id = 102, name = "Math Homework",
                    description = "Solve problems 1-10",
                    due_at = "2025-11-30T23:59:00Z",
                    points_possible = 50.0,
                    html_url = "https://canvas/assignments/102",
                    submission_types = new[] { "online_text_entry" }
                }
            };

            string json = JsonConvert.SerializeObject(assignments);
            var service = CreateService(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });

            var result = await service.GetMissingAssignmentsAsync();
            result.Should().HaveCount(2);
            result[0].Title.Should().Be("History Essay");
            result[0].Source.Should().Be(AssignmentSource.Canvas);
            result[0].Status.Should().Be(AssignmentStatus.Missing);
        }

        [Fact]
        public async Task SubmitAssignmentAsync_ThrowsUnauthorizedSubmissionException_WhenNotApproved()
        {
            var service = CreateService(new HttpResponseMessage(HttpStatusCode.OK));
            var reviewItem = new ReviewItem
            {
                IsApprovedByUser = false,
                SourceAssignment = new Assignment { Id = "101", CourseId = "5", Title = "Test" }
            };
            await Assert.ThrowsAsync<UnauthorizedSubmissionException>(
                () => service.SubmitAssignmentAsync(reviewItem));
        }

        [Fact]
        public async Task GetMissingAssignmentsAsync_ThrowsServiceUnavailableException_OnHttpError()
        {
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("Network unreachable"));
            var httpClient = new HttpClient(handlerMock.Object);
            var service = new CanvasService(httpClient, _mockLog.Object, _mockConfig.Object);
            await Assert.ThrowsAsync<ServiceUnavailableException>(
                () => service.GetMissingAssignmentsAsync());
        }
    }

    // ============================================================
    // OpenAIServiceTests
    // ============================================================

    public class OpenAIServiceTests
    {
        private readonly Mock<ILoggingService> _mockLog = new();
        private readonly Mock<ISecureConfigService> _mockConfig = new();

        public OpenAIServiceTests()
        {
            _mockConfig.Setup(c => c.GetOpenAiApiKeyAsync())
                .ReturnsAsync("sk-test-openai-key-12345");
        }

        private OpenAIService CreateService(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
                });
            return new OpenAIService(
                new HttpClient(handlerMock.Object), _mockLog.Object, _mockConfig.Object);
        }

        [Fact]
        public async Task GenerateAssignmentResponseAsync_ReturnsReviewItem_WithIsApprovedFalse()
        {
            string openAiResponse = @"{
                ""choices"": [{""message"": {""content"": ""This is the completed essay about World War II. The war began in 1939 and ended in 1945. It involved many countries around the world and changed history forever.""}}],
                ""usage"": {""total_tokens"": 150}
            }";

            var service = CreateService(openAiResponse);
            var assignment = new Assignment
            {
                Id = "101",
                Title = "Essay about World War II",
                Description = "Write a short essay about the causes and effects of World War II.",
                CourseName = "World History"
            };

            var reviewItem = await service.GenerateAssignmentResponseAsync(
                assignment, new List<string>());

            reviewItem.Should().NotBeNull();
            reviewItem.IsApprovedByUser.Should().BeFalse("AI content must never be auto-approved.");
            reviewItem.Status.Should().Be(ReviewItemStatus.PendingReview);
            reviewItem.OriginalAiContent.Should().Contain("World War II");
            reviewItem.AiModelUsed.Should().Be("gpt-4o");
            reviewItem.TotalTokensUsed.Should().Be(150);
        }

        [Fact]
        public async Task GenerateAssignmentResponseAsync_ThrowsGuardException_ForEmptyTitle()
        {
            var service = CreateService("{}");
            var assignment = new Assignment
            {
                Id = "101",
                Title = "",
                Description = "Some description",
                CourseName = "Math"
            };
            await Assert.ThrowsAsync<AiHallucinationGuardException>(
                () => service.GenerateAssignmentResponseAsync(assignment, new List<string>()));
        }

        [Fact]
        public async Task GenerateAssignmentResponseAsync_ThrowsGuardException_ForHallucinationResponse()
        {
            string badResponse = @"{
                ""choices"": [{""message"": {""content"": ""As an AI language model, I cannot access your school's specific assignment details or grading rubrics.""}}],
                ""usage"": {""total_tokens"": 40}
            }";
            var service = CreateService(badResponse);
            var assignment = new Assignment
            {
                Id = "101",
                Title = "Chemistry Lab Report",
                Description = "Write a lab report on the titration experiment.",
                CourseName = "Chemistry"
            };
            await Assert.ThrowsAsync<AiHallucinationGuardException>(
                () => service.GenerateAssignmentResponseAsync(assignment, new List<string>()));
        }

        [Fact]
        public async Task GenerateAssignmentResponseAsync_ThrowsServiceUnavailableException_On401()
        {
            var service = CreateService(
                @"{""error"": {""message"": ""Invalid API key""}}",
                HttpStatusCode.Unauthorized);
            var assignment = new Assignment
            {
                Id = "1", Title = "Test Assignment",
                Description = "Do this assignment.", CourseName = "English"
            };
            await Assert.ThrowsAsync<ServiceUnavailableException>(
                () => service.GenerateAssignmentResponseAsync(assignment, new List<string>()));
        }
    }
}

namespace EduAutomation.Tests.ViewModels
{
    // ============================================================
    // ReviewViewModelTests
    // ============================================================

    public class ReviewViewModelTests
    {
        private readonly Mock<ICanvasService> _mockCanvas = new();
        private readonly Mock<ILoggingService> _mockLog = new();

        [Fact]
        public void AddReviewItem_AddsItemToQueue_WithPendingStatus()
        {
            var vm = new ReviewViewModel(_mockCanvas.Object, _mockLog.Object);
            var item = new ReviewItem
            {
                SourceAssignment = new Assignment { Title = "Math Test" },
                Status = ReviewItemStatus.PendingReview
            };
            vm.AddReviewItem(item);
            vm.PendingReviewItems.Should().HaveCount(1);
            vm.SelectedReviewItem.Should().Be(item);
        }

        [Fact]
        public void ApproveContent_SetsIsApprovedByUser_ToTrue()
        {
            var vm = new ReviewViewModel(_mockCanvas.Object, _mockLog.Object);
            var item = new ReviewItem
            {
                SourceAssignment = new Assignment { Title = "Essay" },
                IsApprovedByUser = false
            };
            vm.AddReviewItem(item);
            vm.ApproveContentCommand.Execute(null);
            item.IsApprovedByUser.Should().BeTrue("User explicitly approved the content.");
            item.ApprovedAt.Should().NotBeNull();
            vm.CanSubmit.Should().BeTrue();
        }

        [Fact]
        public void RejectContent_SetsIsApprovedByUser_ToFalse_AndCanSubmitFalse()
        {
            var vm = new ReviewViewModel(_mockCanvas.Object, _mockLog.Object);
            var item = new ReviewItem
            {
                SourceAssignment = new Assignment { Title = "Essay" },
                IsApprovedByUser = true
            };
            vm.AddReviewItem(item);
            vm.RejectContentCommand.Execute(null);
            item.IsApprovedByUser.Should().BeFalse();
            vm.CanSubmit.Should().BeFalse();
            item.Status.Should().Be(ReviewItemStatus.Rejected);
        }

        [Fact]
        public async Task SubmitToCanvasAsync_ShowsError_WhenItemNotApproved()
        {
            var vm = new ReviewViewModel(_mockCanvas.Object, _mockLog.Object);
            var item = new ReviewItem
            {
                SourceAssignment = new Assignment { Title = "Science Project" },
                IsApprovedByUser = false
            };
            vm.AddReviewItem(item);
            vm.CanSubmit.Should().BeFalse();
            await vm.SubmitToCanvasCommand.ExecuteAsync(null);
            vm.HasError.Should().BeTrue();
            _mockCanvas.Verify(c => c.SubmitAssignmentAsync(
                It.IsAny<ReviewItem>(), default), Times.Never);
        }

        [Fact]
        public async Task SubmitToCanvasAsync_CallsCanvasService_WhenApproved()
        {
            _mockCanvas.Setup(c => c.SubmitAssignmentAsync(
                It.IsAny<ReviewItem>(), default)).ReturnsAsync(true);
            var vm = new ReviewViewModel(_mockCanvas.Object, _mockLog.Object);
            var item = new ReviewItem
            {
                SourceAssignment = new Assignment
                {
                    Id = "1", CourseId = "5", Title = "Science Project"
                },
                IsApprovedByUser = true,
                Status = ReviewItemStatus.Approved
            };
            vm.AddReviewItem(item);
            vm.ApproveContentCommand.Execute(null);
            await vm.SubmitToCanvasCommand.ExecuteAsync(null);
            _mockCanvas.Verify(c => c.SubmitAssignmentAsync(
                It.Is<ReviewItem>(r => r.IsApprovedByUser), default), Times.Once);
            vm.PendingReviewItems.Should().BeEmpty("submitted items are removed from the queue.");
        }
    }
}