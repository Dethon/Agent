using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.Mcp;

internal static class ConversationContextMeta
{
    public const string OptionsKey = "ConversationContext";
    public const string MetaKey = "conversationContext";

    public static JsonObject? TryBuild(ChatOptions? options)
    {
        if (options?.AdditionalProperties?.GetValueOrDefault(OptionsKey) is not ConversationContext context)
        {
            return null;
        }

        return new JsonObject
        {
            [MetaKey] = JsonSerializer.SerializeToNode(context, ChannelProtocol.SerializerOptions)
        };
    }
}