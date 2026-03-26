using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelSignalR.Services;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class CreateConversationTool
{
    [McpServerTool(Name = "create_conversation")]
    [Description("Create a new conversation for agent-initiated messages")]
    public static async Task<string> McpRun(
        [Description("Agent identifier")] string agentId,
        [Description("Topic display name")] string topicName,
        [Description("User who initiated")] string sender,
        IServiceProvider services)
    {
        var p = new CreateConversationParams
        {
            AgentId = agentId,
            TopicName = topicName,
            Sender = sender
        };

        var sessionService = services.GetRequiredService<SessionService>();
        var conversationId = await sessionService.CreateConversationAsync(p);
        var topicId = sessionService.GetTopicIdByConversationId(conversationId)!;
        var session = sessionService.GetSessionByConversationId(conversationId);

        // Save topic to Redis so WebChat client can see it in the topic list
        var redisState = services.GetRequiredService<RedisStateService>();
        var topic = new TopicMetadata(
            topicId,
            session!.ChatId,
            session.ThreadId,
            agentId,
            topicName,
            DateTimeOffset.UtcNow,
            LastMessageAt: null,
            SpaceSlug: "default");
        await redisState.SaveTopicAsync(topic);

        // Notify WebChat clients so the topic appears without refresh
        var hubSender = services.GetRequiredService<IHubNotificationSender>();
        var notification = new TopicChangedNotification(
            TopicChangeType.Created, topicId, topic, SpaceSlug: "default");
        await hubSender.SendToGroupAsync("space:default", "OnTopicChanged", notification);

        // Create a stream so send_reply chunks have somewhere to go
        var streamService = services.GetRequiredService<StreamService>();
        streamService.GetOrCreateStream(topicId, topicName, sender, CancellationToken.None);

        return conversationId;
    }
}