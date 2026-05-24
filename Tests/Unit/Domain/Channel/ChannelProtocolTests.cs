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
}