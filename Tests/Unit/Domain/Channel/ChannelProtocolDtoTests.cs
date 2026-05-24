using System.Text.Json;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Shouldly;

namespace Tests.Unit.Domain.Channel;

public class ChannelProtocolDtoTests
{
    [Fact]
    public void ChannelMessageNotification_RoundTripsThroughProtocol()
    {
        var original = new ChannelMessageNotification
        {
            ConversationId = "c1",
            Sender = "scheduler",
            Content = "run",
            AgentId = "jonas",
            ReplyTo = [new ReplyTarget("signalr", null), new ReplyTarget("telegram", "t-1")],
            Origin = new MessageOrigin("schedule", "morning-news"),
            Timestamp = DateTimeOffset.UnixEpoch
        };

        var element = JsonSerializer.SerializeToElement(original, ChannelProtocol.SerializerOptions);
        var copy = ChannelProtocol.Deserialize<ChannelMessageNotification>(element);

        copy.ShouldNotBeNull();
        copy!.AgentId.ShouldBe("jonas");
        copy.ReplyTo!.Count.ShouldBe(2);
        copy.ReplyTo[0].ChannelId.ShouldBe("signalr");
        copy.ReplyTo[0].ConversationId.ShouldBeNull();
        copy.Origin!.Kind.ShouldBe("schedule");
        copy.Origin.ScheduleId.ShouldBe("morning-news");
        element.GetProperty("replyTo")[1].GetProperty("conversationId").GetString().ShouldBe("t-1");
    }

    [Fact]
    public void ChannelCancelNotification_RoundTripsThroughProtocol()
    {
        var original = new ChannelCancelNotification
        {
            ConversationId = "c1",
            AgentId = "jonas",
            Timestamp = DateTimeOffset.UnixEpoch
        };

        var element = JsonSerializer.SerializeToElement(original, ChannelProtocol.SerializerOptions);
        var copy = ChannelProtocol.Deserialize<ChannelCancelNotification>(element);

        copy.ShouldNotBeNull();
        copy!.ConversationId.ShouldBe("c1");
        copy.AgentId.ShouldBe("jonas");
    }

    [Fact]
    public void ToArguments_WithRegisterAgentsParams_ProducesAgentsArray()
    {
        var p = new RegisterAgentsParams
        {
            Agents = [new AgentCatalogEntry("jack", "Jack", "Downloads")]
        };

        var args = ChannelProtocol.ToArguments(p);

        args.Keys.ShouldBe(["agents"]);
        JsonSerializer.Serialize(args["agents"]).ShouldContain("\"id\":\"jack\"");
    }

    [Fact]
    public void ToArguments_WithCreateConversationParams_ProducesExpectedKeys()
    {
        var p = new CreateConversationParams
        {
            AgentId = "jonas",
            TopicName = "Topic",
            Sender = "user"
        };

        var args = ChannelProtocol.ToArguments(p);

        args.Keys.OrderBy(k => k).ShouldBe(["agentId", "sender", "topicName"]);
    }

    [Fact]
    public void ToArguments_WithRequestApprovalParams_SerializesRequestsAsArrayPreservingMessageId()
    {
        var p = new RequestApprovalParams
        {
            ConversationId = "c1",
            Mode = ApprovalMode.Request,
            Requests = [new ToolApprovalRequest("m1", "mcp__x__do", new Dictionary<string, object?> { ["k"] = "v" })]
        };

        var args = ChannelProtocol.ToArguments(p);
        var requests = (JsonElement)args["requests"]!;

        requests.ValueKind.ShouldBe(JsonValueKind.Array);
        requests[0].GetProperty("messageId").GetString().ShouldBe("m1");
        requests[0].GetProperty("toolName").GetString().ShouldBe("mcp__x__do");
        JsonSerializer.Serialize(args["mode"]).ShouldBe("\"Request\"");
    }
}