using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;

namespace Domain.Channels;

// The namespace a stateful tool server caches per-caller state under.
//
// Before the 2026-07-28 protocol this was the MCP session id -- one per ThreadSession, hence one
// per conversation. Sessions are gone, and the only remaining connection-level identifier is the
// client name, which is the *agent* name: keying on it would collapse every user and conversation
// of an agent into a single bucket. The conversation now rides in each tools/call's _meta, so
// scope it explicitly and refuse to guess when it is absent -- a shared-bucket fallback leaks one
// conversation's state into another, and a per-request fallback silently severs multi-call flows
// (file_search -> download_file). Both fail invisibly; a ToolError does not.
public static class ConversationScope
{
    public static string Build(string agentId, string conversationId) => $"{agentId}:{conversationId}";

    public static ConversationContext? Parse(JsonObject? meta)
        => meta?[ChannelProtocol.ConversationContextMetaKey]
            ?.Deserialize<ConversationContext>(ChannelProtocol.SerializerOptions);

    public static bool TryResolve(JsonObject? meta, out string scope)
    {
        if (Parse(meta) is not { } context)
        {
            scope = string.Empty;
            return false;
        }

        scope = Build(context.AgentId, context.ConversationId);
        return true;
    }
}