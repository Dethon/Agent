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

    // _meta key under which the agent's MCP tool wrapper attaches the current turn's
    // ConversationContext to every tools/call; dual-role servers read it for routing.
    public const string ConversationContextMetaKey = "conversationContext";

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