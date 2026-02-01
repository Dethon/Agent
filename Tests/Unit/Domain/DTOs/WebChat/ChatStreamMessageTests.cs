using Domain.DTOs.WebChat;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.WebChat;

public sealed class ChatStreamMessageTests
{
    [Fact]
    public void ChatStreamMessage_WithUserMessage_IndicatesUserRole()
    {
        var message = new ChatStreamMessage
        {
            Content = "Hello",
            UserMessage = new UserMessageInfo("alice", null)
        };

        message.UserMessage.ShouldNotBeNull();
        message.UserMessage.SenderId.ShouldBe("alice");
    }

    [Fact]
    public void ChatStreamMessage_WithoutUserMessage_IndicatesAssistantRole()
    {
        var message = new ChatStreamMessage
        {
            Content = "Hello"
        };

        message.UserMessage.ShouldBeNull();
    }

    [Fact]
    public void ChatStreamMessage_WithTimestamp_StoresValue()
    {
        var ts = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var message = new ChatStreamMessage
        {
            Content = "Hello",
            Timestamp = ts
        };

        message.Timestamp.ShouldBe(ts);
    }

    [Fact]
    public void ChatStreamMessage_WithoutTimestamp_DefaultsToNull()
    {
        var message = new ChatStreamMessage { Content = "Hello" };

        message.Timestamp.ShouldBeNull();
    }
}