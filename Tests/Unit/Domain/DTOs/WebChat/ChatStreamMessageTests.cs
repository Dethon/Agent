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
}