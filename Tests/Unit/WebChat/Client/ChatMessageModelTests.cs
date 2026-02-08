using Shouldly;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client;

public sealed class ChatMessageModelTests
{
    [Fact]
    public void ChatMessageModel_HasMessageIdProperty()
    {
        var message = new ChatMessageModel { MessageId = "msg-123" };
        message.MessageId.ShouldBe("msg-123");
    }

    [Fact]
    public void ChatMessageModel_MessageIdDefaultsToNull()
    {
        var message = new ChatMessageModel();
        message.MessageId.ShouldBeNull();
    }
}
