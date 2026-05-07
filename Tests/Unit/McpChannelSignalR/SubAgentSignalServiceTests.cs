using Domain.Contracts;
using Domain.DTOs.WebChat;
using McpChannelSignalR.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class SubAgentSignalServiceTests
{
    private readonly SessionService _sessionService = new();
    private readonly Mock<IHubNotificationSender> _hubSender = new();
    private readonly SubAgentSignalService _sut;

    public SubAgentSignalServiceTests()
    {
        _sut = new SubAgentSignalService(
            _sessionService,
            _hubSender.Object,
            NullLogger<SubAgentSignalService>.Instance);
    }

    [Fact]
    public async Task AnnounceAsync_WithKnownConversation_SendsToSpaceGroup()
    {
        _sessionService.StartSession("topic1", "agent1", 100, 200, spaceSlug: "default");

        await _sut.AnnounceAsync("100:200", "worker-1", "researcher");

        _hubSender.Verify(s =>
            s.SendToGroupAsync(
                "space:default",
                "OnSubAgentAnnounced",
                It.Is<SubAgentAnnouncedNotification>(n =>
                    n.TopicId == "topic1" &&
                    n.Handle == "worker-1" &&
                    n.SubAgentId == "researcher"),
                default),
            Times.Once);
    }

    [Fact]
    public async Task AnnounceAsync_WithNoSession_SendsToAll()
    {
        await _sut.AnnounceAsync("unknown:conv", "worker-1", "researcher");

        _hubSender.Verify(s =>
            s.SendAsync(
                "OnSubAgentAnnounced",
                It.Is<SubAgentAnnouncedNotification>(n =>
                    n.Handle == "worker-1" &&
                    n.SubAgentId == "researcher"),
                default),
            Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_WithKnownConversation_SendsToSpaceGroup()
    {
        _sessionService.StartSession("topic2", "agent1", 300, 400, spaceSlug: "workspace");

        await _sut.UpdateAsync("300:400", "worker-2", "Completed");

        _hubSender.Verify(s =>
            s.SendToGroupAsync(
                "space:workspace",
                "OnSubAgentUpdated",
                It.Is<SubAgentUpdatedNotification>(n =>
                    n.TopicId == "topic2" &&
                    n.Handle == "worker-2" &&
                    n.Status == "Completed"),
                default),
            Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_WithNoSession_SendsToAll()
    {
        await _sut.UpdateAsync("unknown:conv", "worker-2", "Failed");

        _hubSender.Verify(s =>
            s.SendAsync(
                "OnSubAgentUpdated",
                It.Is<SubAgentUpdatedNotification>(n =>
                    n.Handle == "worker-2" &&
                    n.Status == "Failed"),
                default),
            Times.Once);
    }
}
