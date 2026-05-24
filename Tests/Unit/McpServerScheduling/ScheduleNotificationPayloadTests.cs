using System.Text.Json;
using Domain.DTOs.Channel;
using McpServerScheduling.Services;
using Shouldly;
using Xunit;

namespace Tests.Unit.McpServerScheduling;

public class ScheduleNotificationPayloadTests
{
    [Fact]
    public void BuildPayload_WithReplyToAndOrigin_ProducesChannelMessageNotification()
    {
        ChannelMessageNotification payload = ScheduleNotificationEmitter.BuildPayload(
            conversationId: "fire-1",
            sender: "scheduler",
            content: "do it",
            agentId: "jonas",
            replyTo: [new ReplyTarget("signalr", null), new ReplyTarget("telegram", "t-1")],
            origin: new MessageOrigin("schedule", "morning-news"));

        payload.AgentId.ShouldBe("jonas");
        payload.ReplyTo!.Count.ShouldBe(2);
        payload.Origin!.ScheduleId.ShouldBe("morning-news");

        var json = JsonSerializer.Serialize(payload, ChannelProtocol.SerializerOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("replyTo")[1].GetProperty("conversationId").GetString().ShouldBe("t-1");
        root.GetProperty("origin").GetProperty("kind").GetString().ShouldBe("schedule");
    }
}