using Domain.DTOs;
using Shouldly;

namespace Tests.Unit.Domain;

public class ChannelMessageTests
{
    [Fact]
    public void ChannelMessage_WithAllProperties_SetsCorrectly()
    {
        var msg = new ChannelMessage
        {
            ConversationId = "conv-123",
            Content = "Hello",
            Sender = "alice",
            ChannelId = "telegram",
            AgentId = "jack"
        };

        msg.ConversationId.ShouldBe("conv-123");
        msg.Content.ShouldBe("Hello");
        msg.Sender.ShouldBe("alice");
        msg.ChannelId.ShouldBe("telegram");
        msg.AgentId.ShouldBe("jack");
    }

    [Fact]
    public void ChannelMessage_AgentId_DefaultsToNull()
    {
        var msg = new ChannelMessage
        {
            ConversationId = "conv-123",
            Content = "Hello",
            Sender = "alice",
            ChannelId = "signalr"
        };

        msg.AgentId.ShouldBeNull();
    }

    [Fact]
    public void ReplyContentType_Constants_HaveExpectedValues()
    {
        ReplyContentType.Text.ShouldBe("text");
        ReplyContentType.Reasoning.ShouldBe("reasoning");
        ReplyContentType.ToolCall.ShouldBe("tool_call");
        ReplyContentType.Error.ShouldBe("error");
        ReplyContentType.StreamComplete.ShouldBe("stream_complete");
    }
}
