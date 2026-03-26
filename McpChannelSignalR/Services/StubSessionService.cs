using Domain.DTOs.Channel;

namespace McpChannelSignalR.Services;

public sealed class StubSessionService(ILogger<StubSessionService> logger) : ISessionService
{
    public Task<string> CreateConversationAsync(CreateConversationParams p)
    {
        var conversationId = Guid.NewGuid().ToString();
        logger.LogDebug(
            "CreateConversation: agent={AgentId}, topic={TopicName}, sender={Sender}, id={ConversationId}",
            p.AgentId, p.TopicName, p.Sender, conversationId);
        return Task.FromResult(conversationId);
    }
}