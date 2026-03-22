using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Clients.Channels;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class ChannelToolApprovalHandlerTests
{
    private readonly Mock<IChannelConnection> _channel = new();
    private const string ConversationId = "conv-123";

    [Fact]
    public async Task RequestApprovalAsync_DelegatesToChannel()
    {
        var requests = new List<ToolApprovalRequest>
        {
            new("msg-1", "search", new Dictionary<string, object?> { ["query"] = "test" })
        };
        _channel
            .Setup(c => c.RequestApprovalAsync(ConversationId, requests, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolApprovalResult.Approved);

        var sut = new ChannelToolApprovalHandler(_channel.Object, ConversationId);
        var result = await sut.RequestApprovalAsync(requests, CancellationToken.None);

        result.ShouldBe(ToolApprovalResult.Approved);
        _channel.Verify(c => c.RequestApprovalAsync(ConversationId, requests, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyAutoApprovedAsync_DelegatesToChannel()
    {
        var requests = new List<ToolApprovalRequest>
        {
            new("msg-1", "read_file", new Dictionary<string, object?> { ["path"] = "/tmp" })
        };

        var sut = new ChannelToolApprovalHandler(_channel.Object, ConversationId);
        await sut.NotifyAutoApprovedAsync(requests, CancellationToken.None);

        _channel.Verify(c => c.NotifyAutoApprovedAsync(ConversationId, requests, It.IsAny<CancellationToken>()), Times.Once);
    }
}
