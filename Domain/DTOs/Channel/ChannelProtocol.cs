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

    // The voice channel attaches to a shared conversation rather than owning one: its
    // create_conversation hands back the id it was given (it has no persisted TopicId of its
    // own). Delivery fan-out orders these targets last so a topic-owning channel always anchors
    // the shared id. Matches the "voice"/"voice:<satellite>" deliverTo convention (SchedulingPrompt).
    public const string VoiceChannelId = "voice";

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