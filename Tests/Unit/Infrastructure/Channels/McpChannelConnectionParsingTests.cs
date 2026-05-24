using System.Text.Json;
using Infrastructure.Clients.Channels;
using Shouldly;

namespace Tests.Unit.Infrastructure.Channels;

public class McpChannelConnectionParsingTests
{
    private static JsonElement Json(string s) => JsonSerializer.Deserialize<JsonElement>(s);

    [Fact]
    public async Task HandleChannelMessageNotification_WithReplyToAndOrigin_ParsesThem()
    {
        var conn = new McpChannelConnection("scheduling");
        conn.HandleChannelMessageNotification(Json("""
        {
          "conversationId": "c1",
          "content": "run it",
          "sender": "scheduler",
          "agentId": "jonas",
          "replyTo": [{"channelId":"signalr","conversationId":null},{"channelId":"telegram","conversationId":"t-1"}],
          "origin": {"kind":"schedule","scheduleId":"morning-news"}
        }
        """));

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var msg in conn.Messages.WithCancellation(cts.Token))
        {
            msg.AgentId.ShouldBe("jonas");
            msg.ReplyTo!.Count.ShouldBe(2);
            msg.ReplyTo[0].ChannelId.ShouldBe("signalr");
            msg.ReplyTo[0].ConversationId.ShouldBeNull();
            msg.ReplyTo[1].ChannelId.ShouldBe("telegram");
            msg.ReplyTo[1].ConversationId.ShouldBe("t-1");
            msg.Origin!.Kind.ShouldBe("schedule");
            msg.Origin.ScheduleId.ShouldBe("morning-news");
            break;
        }
    }

    [Fact]
    public async Task HandleChannelMessageNotification_WithoutReplyToAndOrigin_LeavesThemNull()
    {
        var conn = new McpChannelConnection("signalr");
        conn.HandleChannelMessageNotification(Json("""
        {"conversationId":"c1","content":"hi","sender":"user"}
        """));

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var msg in conn.Messages.WithCancellation(cts.Token))
        {
            msg.ReplyTo.ShouldBeNull();
            msg.Origin.ShouldBeNull();
            break;
        }
    }

    [Fact]
    public async Task HandleChannelMessageNotification_WithMalformedPayload_WritesNothing()
    {
        var conn = new McpChannelConnection("signalr");
        conn.HandleChannelMessageNotification(Json("""{"sender":"user"}"""));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var read = async () =>
        {
            await foreach (var _ in conn.Messages.WithCancellation(cts.Token))
            {
                return;
            }
        };

        await Should.ThrowAsync<OperationCanceledException>(read);
    }
}