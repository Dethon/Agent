using Domain.DTOs;
using Domain.DTOs.Channel;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.DTOs;

public class ChannelMessageTests
{
    [Fact]
    public void ChannelMessage_WithReplyToAndOrigin_ExposesValues()
    {
        var message = new ChannelMessage
        {
            ConversationId = "c1",
            Content = "hi",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas",
            ReplyTo = [new ReplyTarget("signalr", null), new ReplyTarget("telegram", "t-42")],
            Origin = new MessageOrigin("schedule", "morning-news")
        };

        message.ReplyTo!.Count.ShouldBe(2);
        message.ReplyTo[0].ChannelId.ShouldBe("signalr");
        message.ReplyTo[0].ConversationId.ShouldBeNull();
        message.Origin!.Kind.ShouldBe("schedule");
        message.Origin.ScheduleId.ShouldBe("morning-news");
    }

    [Fact]
    public void ChannelMessage_WithoutReplyToAndOrigin_DefaultsToNull()
    {
        var message = new ChannelMessage
        {
            ConversationId = "c1",
            Content = "hi",
            Sender = "u",
            ChannelId = "signalr"
        };

        message.ReplyTo.ShouldBeNull();
        message.Origin.ShouldBeNull();
    }
}