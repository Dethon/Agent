using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Monitor;

public class ChatMonitorScheduleMetricsTests
{
    [Fact]
    public void BuildScheduleEvent_WithScheduleOrigin_ReturnsEvent()
    {
        var msg = new ChannelMessage
        {
            ConversationId = "c", Content = "do the thing", Sender = "scheduler", ChannelId = "scheduling",
            AgentId = "jonas", Origin = new MessageOrigin("schedule", "morning-news")
        };

        var evt = ChatMonitor.BuildScheduleEvent(msg, durationMs: 1234, success: true, error: null);

        evt.ShouldNotBeNull();
        evt.ScheduleId.ShouldBe("morning-news");
        evt.AgentId.ShouldBe("jonas");
        evt.Prompt.ShouldBe("do the thing");
        evt.DurationMs.ShouldBe(1234);
        evt.Success.ShouldBeTrue();
    }

    [Fact]
    public void BuildScheduleEvent_WithNonScheduleMessage_ReturnsNull()
    {
        var msg = new ChannelMessage { ConversationId = "c", Content = "hi", Sender = "u", ChannelId = "signalr" };
        ChatMonitor.BuildScheduleEvent(msg, 1, true, null).ShouldBeNull();
    }
}