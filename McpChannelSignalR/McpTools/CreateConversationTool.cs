using System.ComponentModel;
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

        // Save topic to Redis so WebChat client can see it in the topic list
        var redisState = services.GetRequiredService<RedisStateService>();
        var session = sessionService.GetSessionByConversationId(conversationId);
        if (session is not null)
        {
            var topicId = sessionService.GetTopicIdByConversationId(conversationId)!;
            var topic = new TopicMetadata(
                topicId,
                session.ChatId,
                session.ThreadId,
                agentId,
                topicName,
                DateTimeOffset.UtcNow,
                LastMessageAt: null,
                SpaceSlug: "default");
            await redisState.SaveTopicAsync(topic);
        }

        // Create a stream so send_reply chunks have somewhere to go
        var streamService = services.GetRequiredService<StreamService>();
        streamService.GetOrCreateStream(
            sessionService.GetTopicIdByConversationId(conversationId)!,
            topicName,
            sender,
            CancellationToken.None);

        return conversationId;
    }
}
