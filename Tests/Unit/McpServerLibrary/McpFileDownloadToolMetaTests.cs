using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using McpServerLibrary.McpTools;
using Shouldly;

namespace Tests.Unit.McpServerLibrary;

public class McpFileDownloadToolMetaTests
{
    [Fact]
    public void ParseConversationContext_NullMeta_ReturnsNull()
    {
        var result = McpFileDownloadTool.ParseConversationContext(null);
        result.ShouldBeNull();
    }

    [Fact]
    public void ParseConversationContext_EmptyMeta_ReturnsNull()
    {
        var meta = new JsonObject();
        var result = McpFileDownloadTool.ParseConversationContext(meta);
        result.ShouldBeNull();
    }

    [Fact]
    public void ParseConversationContext_MetaWithoutConversationContextKey_ReturnsNull()
    {
        var meta = new JsonObject { ["other"] = "value" };
        var result = McpFileDownloadTool.ParseConversationContext(meta);
        result.ShouldBeNull();
    }

    [Fact]
    public void ParseConversationContext_RoundTripsConversationContext()
    {
        var context = new ConversationContext(
            AgentId: "agent-1",
            ConversationId: "conv-abc",
            UserId: "user-42",
            Origin: new ReplyTarget("signalr", "conv-abc"));

        var node = JsonSerializer.SerializeToNode(context, ChannelProtocol.SerializerOptions);
        var meta = new JsonObject { ["conversationContext"] = node };

        var result = McpFileDownloadTool.ParseConversationContext(meta);

        result.ShouldNotBeNull();
        result.AgentId.ShouldBe("agent-1");
        result.ConversationId.ShouldBe("conv-abc");
        result.UserId.ShouldBe("user-42");
        result.Origin.ChannelId.ShouldBe("signalr");
        result.Origin.ConversationId.ShouldBe("conv-abc");
    }

    [Fact]
    public void ParseConversationContext_MetaWithNullConversationContextNode_ReturnsNull()
    {
        var meta = new JsonObject { ["conversationContext"] = null };
        var result = McpFileDownloadTool.ParseConversationContext(meta);
        result.ShouldBeNull();
    }
}