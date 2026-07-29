using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public static class ChannelProtocol
{
    public const string MessageNotification = "notifications/channel/message";
    public const string CancelNotification = "notifications/channel/cancel";
    public const string SendReplyTool = "send_reply";
    public const string RequestApprovalTool = "request_approval";
    public const string CreateConversationTool = "create_conversation";
    public const string RegisterAgentsTool = "register_agents";
    public const string ReceiveTool = "channel_receive";

    // How long a channel_receive call may be held open server-side before returning an empty
    // batch. Verified safe: a 45s hold completes on the SDK's default client timeout, and no
    // reverse proxy sits between the agent and a channel server (ChannelEndpoints are
    // container-to-container; Caddy only fronts the browser-facing /hubs/* route).
    public const int DefaultReceiveWaitMs = 30_000;

    // _meta key under which the agent's MCP tool wrapper attaches the current turn's
    // ConversationContext to every tools/call; dual-role servers read it for routing.
    public const string ConversationContextMetaKey = "conversationContext";

    // Sender attributed to channel/message notifications the system originates on a user's
    // behalf rather than the user themselves — e.g. the /cancel command and download-completion
    // alerts. Keeps these off the initiating user's identity (memory scoping, attribution).
    public const string SystemSender = "system";

    // The agent's channel connections identify themselves as "channel-<channelId>"; tool sessions
    // use the agent name. Dual-role servers must only count channel clients as delivery targets —
    // tool sessions silently drop channel/message notifications.
    public const string ChannelClientNamePrefix = "channel-";

    public static bool IsChannelClientName(string? clientName)
        => clientName?.StartsWith(ChannelClientNamePrefix, StringComparison.Ordinal) == true;

    // A TypeInfoResolver is mandatory: the MCP SDK's SendNotificationAsync calls
    // JsonSerializerOptions.MakeReadOnly() on these options, which throws if no resolver is set.
    // Without it, channel emitters silently failed to deliver channel/message notifications.
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter() }
    };

    public static IReadOnlyDictionary<string, object?> ToArguments<T>(T value)
    {
        using var document = JsonSerializer.SerializeToDocument(value, SerializerOptions);
        return document.RootElement
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
    }

    public static T? Deserialize<T>(JsonElement element) => element.Deserialize<T>(SerializerOptions);
}