using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.Extensions;

namespace Tests.Unit.WebChat.Client;

public sealed class ChatHistoryMessageExtensionsTests
{
    [Fact]
    public void ToChatMessageModel_MapsAllProperties()
    {
        var history = new ChatHistoryMessage(
            MessageId: "msg-123",
            Role: "assistant",
            Content: "Hello",
            SenderId: "agent-1",
            Timestamp: new DateTimeOffset(2026, 1, 28, 12, 0, 0, TimeSpan.Zero));

        var result = history.ToChatMessageModel();

        result.MessageId.ShouldBe("msg-123");
        result.Role.ShouldBe("assistant");
        result.Content.ShouldBe("Hello");
        result.SenderId.ShouldBe("agent-1");
        result.Timestamp.ShouldBe(new DateTimeOffset(2026, 1, 28, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ToChatMessageModel_HandlesNullMessageId()
    {
        var history = new ChatHistoryMessage(null, "user", "Hi", null, null);

        var result = history.ToChatMessageModel();

        result.MessageId.ShouldBeNull();
    }
}
