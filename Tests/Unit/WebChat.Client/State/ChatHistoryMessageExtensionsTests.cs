using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.Extensions;

namespace Tests.Unit.WebChat.Client.State;

public sealed class ChatHistoryMessageExtensionsTests
{
    [Theory]
    [InlineData("msg-123", "assistant", "Hello", "agent-1")]
    [InlineData(null, "user", "Hi", null)]
    public void ToChatMessageModel_MapsProperties(string? messageId, string role, string content, string? senderId)
    {
        var ts = messageId != null ? new DateTimeOffset(2026, 1, 28, 12, 0, 0, TimeSpan.Zero) : (DateTimeOffset?)null;
        var history = new ChatHistoryMessage(MessageId: messageId, Role: role, Content: content, SenderId: senderId, Timestamp: ts);

        var result = history.ToChatMessageModel();

        result.MessageId.ShouldBe(messageId);
        result.Role.ShouldBe(role);
        result.Content.ShouldBe(content);
        result.SenderId.ShouldBe(senderId);
        result.Timestamp.ShouldBe(ts);
    }
}