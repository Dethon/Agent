using Domain.DTOs.Channel;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

public class ConversationContextMetaTests
{
    private static readonly ConversationContext _context = new(
        "jack", "conv-9", "fran", new ReplyTarget("signalr", "conv-9"));

    [Fact]
    public void TryBuild_WithContextInOptions_ProducesMetaJson()
    {
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ConversationContextMeta.OptionsKey] = _context
            }
        };

        var meta = ConversationContextMeta.TryBuild(options).ShouldNotBeNull();

        var node = meta[ConversationContextMeta.MetaKey].ShouldNotBeNull();
        node["conversationId"]!.GetValue<string>().ShouldBe("conv-9");
        node["agentId"]!.GetValue<string>().ShouldBe("jack");
        node["userId"]!.GetValue<string>().ShouldBe("fran");
        node["origin"]!["channelId"]!.GetValue<string>().ShouldBe("signalr");
    }

    [Fact]
    public void TryBuild_NullOptionsOrMissingKey_ReturnsNull()
    {
        ConversationContextMeta.TryBuild(null).ShouldBeNull();
        ConversationContextMeta.TryBuild(new ChatOptions()).ShouldBeNull();
    }
}