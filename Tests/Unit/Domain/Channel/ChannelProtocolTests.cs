using System.Text.Json;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Shouldly;

namespace Tests.Unit.Domain.Channel;

public class ChannelProtocolTests
{
    [Fact]
    public void ToArguments_WithSendReplyParams_ProducesCamelCaseKeysAndStringEnum()
    {
        var p = new SendReplyParams
        {
            ConversationId = "c1",
            Content = "hi",
            ContentType = ReplyContentType.Text,
            IsComplete = true,
            MessageId = "m1"
        };

        var args = ChannelProtocol.ToArguments(p);

        args.Keys.OrderBy(k => k)
            .ShouldBe(["content", "contentType", "conversationId", "isComplete", "messageId"]);
        JsonSerializer.Serialize(args["contentType"]).ShouldBe("\"Text\"");
    }

    [Fact]
    public void Deserialize_WithCamelCasePayload_ReadsTypedDto()
    {
        var element = JsonSerializer.Deserialize<JsonElement>(
            """{"conversationId":"c1","content":"hi","contentType":"Reasoning","isComplete":false,"messageId":null}""");

        var p = ChannelProtocol.Deserialize<SendReplyParams>(element);

        p.ShouldNotBeNull();
        p!.ConversationId.ShouldBe("c1");
        p.ContentType.ShouldBe(ReplyContentType.Reasoning);
        p.IsComplete.ShouldBeFalse();
        p.MessageId.ShouldBeNull();
    }

    [Fact]
    public void NameConstants_MatchWireProtocol()
    {
        ChannelProtocol.MessageNotification.ShouldBe("notifications/channel/message");
        ChannelProtocol.CancelNotification.ShouldBe("notifications/channel/cancel");
        ChannelProtocol.SendReplyTool.ShouldBe("send_reply");
        ChannelProtocol.RequestApprovalTool.ShouldBe("request_approval");
        ChannelProtocol.CreateConversationTool.ShouldBe("create_conversation");
        ChannelProtocol.RegisterAgentsTool.ShouldBe("register_agents");
    }

    [Fact]
    public void ToArgumentsThenDeserialize_RoundTripsSendReplyParams()
    {
        var original = new SendReplyParams
        {
            ConversationId = "c1",
            Content = "hi",
            ContentType = ReplyContentType.ToolCall,
            IsComplete = true,
            MessageId = "m1"
        };

        var args = ChannelProtocol.ToArguments(original);
        var element = JsonSerializer.SerializeToElement(args, ChannelProtocol.SerializerOptions);
        var roundTripped = ChannelProtocol.Deserialize<SendReplyParams>(element);

        roundTripped.ShouldNotBeNull();
        roundTripped!.ConversationId.ShouldBe("c1");
        roundTripped.Content.ShouldBe("hi");
        roundTripped.ContentType.ShouldBe(ReplyContentType.ToolCall);
        roundTripped.IsComplete.ShouldBeTrue();
        roundTripped.MessageId.ShouldBe("m1");
    }

    [Fact]
    public void Serialize_DownloadCompletionNotification_RoundTripsWithStringEnumOrigin()
    {
        var payload = new ChannelMessageNotification
        {
            ConversationId = "conv-7",
            Sender = "fran",
            Content = "[download-complete] ...",
            AgentId = "jack",
            ReplyTo = [new ReplyTarget("signalr", "conv-7")],
            Origin = new MessageOrigin(MessageOriginKind.Download, null),
            Timestamp = DateTimeOffset.UtcNow
        };

        var element = JsonSerializer.SerializeToElement(payload, ChannelProtocol.SerializerOptions);
        var restored = ChannelProtocol.Deserialize<ChannelMessageNotification>(element).ShouldNotBeNull();

        restored.Origin.ShouldBe(new MessageOrigin(MessageOriginKind.Download, null));
        restored.ReplyTo.ShouldBe([new ReplyTarget("signalr", "conv-7")]);
        element.GetProperty("origin").GetProperty("kind").GetString().ShouldBe("Download");
    }

    [Fact]
    public void SerializerOptions_CanBeMarkedReadOnly_AsTheMcpSdkNotificationPathRequires()
    {
        // Regression: the MCP SDK's SendNotificationAsync calls JsonSerializerOptions.MakeReadOnly()
        // on the options it is handed, which throws when no TypeInfoResolver is set. Channel emitters
        // pass ChannelProtocol.SerializerOptions there, so without a resolver every channel/message
        // emit threw and was swallowed — the agent never saw inbound messages and never replied.
        var options = new JsonSerializerOptions(ChannelProtocol.SerializerOptions);

        Should.NotThrow(() => options.MakeReadOnly());
    }
}