using System.Text.Json;
using Domain.DTOs.Channel;
using McpServerScheduling.Services;
using Shouldly;
using Xunit;

namespace Tests.Unit.McpServerScheduling;

public class ScheduleNotificationPayloadTests
{
    [Fact]
    public void BuildPayload_WithReplyToAndOrigin_SerializesExpectedWireShape()
    {
        var node = ScheduleNotificationEmitter.BuildPayload(
            conversationId: "fire-1",
            sender: "scheduler",
            content: "do it",
            agentId: "jonas",
            replyTo: [new ReplyTarget("signalr", null), new ReplyTarget("telegram", "t-1")],
            origin: new MessageOrigin("schedule", "morning-news"));

        var json = JsonSerializer.Serialize(node,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("agentId").GetString().ShouldBe("jonas");
        root.GetProperty("replyTo").GetArrayLength().ShouldBe(2);
        root.GetProperty("origin").GetProperty("kind").GetString().ShouldBe("schedule");
        root.GetProperty("replyTo")[0].GetProperty("channelId").GetString().ShouldBe("signalr");
        root.GetProperty("replyTo")[1].GetProperty("conversationId").GetString().ShouldBe("t-1");
        root.GetProperty("origin").GetProperty("scheduleId").GetString().ShouldBe("morning-news");
    }
}