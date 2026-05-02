using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelSignalR.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class ApprovalServiceTests : IDisposable
{
    private readonly SessionService _sessionService = new();
    private readonly StreamService _streamService;
    private readonly Mock<IHubNotificationSender> _hubSender = new();
    private readonly ApprovalService _sut;

    public ApprovalServiceTests()
    {
        _streamService = new StreamService(
            _sessionService,
            new Mock<IPushNotificationService>().Object,
            new Mock<ILogger<StreamService>>().Object);

        _sut = new ApprovalService(
            _streamService,
            _sessionService,
            _hubSender.Object,
            new Mock<ILogger<ApprovalService>>().Object);
    }

    private static string SerializeRequests(params ToolApprovalRequest[] requests) =>
        JsonSerializer.Serialize(requests);

    [Fact]
    public async Task RequestApprovalAsync_ApprovalGranted_ReturnsApproved()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        _streamService.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);

        var requests = SerializeRequests(
            new ToolApprovalRequest("msg-1", "mcp__server__tool", new Dictionary<string, object?> { ["key"] = "val" }));

        var approvalTask = _sut.RequestApprovalAsync(
            new RequestApprovalParams { ConversationId = "100:200", Mode = "request", Requests = requests });

        var pending = _sut.GetPendingApprovalForTopic("topic1");
        pending.ShouldNotBeNull();
        _sut.IsApprovalPending(pending.ApprovalId).ShouldBeTrue();

        await _sut.RespondToApprovalAsync(pending.ApprovalId, "approved");

        var result = await approvalTask;
        result.ShouldBe("approved");
    }

    [Fact]
    public async Task RequestApprovalAsync_Rejected_ReturnsRejected()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        _streamService.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);

        var requests = SerializeRequests(
            new ToolApprovalRequest("msg-1", "tool", new Dictionary<string, object?>()));

        var approvalTask = _sut.RequestApprovalAsync(
            new RequestApprovalParams { ConversationId = "100:200", Mode = "request", Requests = requests });

        var pending = _sut.GetPendingApprovalForTopic("topic1");
        await _sut.RespondToApprovalAsync(pending!.ApprovalId, "rejected");

        var result = await approvalTask;
        result.ShouldBe("rejected");
    }

    [Fact]
    public async Task CancelPendingApprovalsForTopic_RejectsAll()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        _streamService.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);

        var requests = SerializeRequests(
            new ToolApprovalRequest("msg-1", "tool", new Dictionary<string, object?>()));

        var approvalTask = _sut.RequestApprovalAsync(
            new RequestApprovalParams { ConversationId = "100:200", Mode = "request", Requests = requests });

        _sut.CancelPendingApprovalsForTopic("topic1");

        var result = await approvalTask;
        result.ShouldBe("rejected");
    }

    [Fact]
    public async Task NotifyAutoApprovedAsync_WritesToStream()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200);
        var (channel, _) = _streamService.GetOrCreateStream("topic1", "prompt", "user1", CancellationToken.None);
        var reader = channel.Subscribe();

        var requests = SerializeRequests(
            new ToolApprovalRequest("msg-1", "search", new Dictionary<string, object?> { ["q"] = "test" }));

        await _sut.NotifyAutoApprovedAsync(
            new RequestApprovalParams { ConversationId = "100:200", Mode = "notify", Requests = requests });

        var msg = await reader.ReadAsync();
        msg.ToolCalls.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void GetPendingApprovalForTopic_NoPending_ReturnsNull()
    {
        _sut.GetPendingApprovalForTopic("nonexistent").ShouldBeNull();
    }

    public void Dispose() => _streamService.Dispose();
}