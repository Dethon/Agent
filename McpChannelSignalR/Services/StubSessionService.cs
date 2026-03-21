using Microsoft.Extensions.Logging;

namespace McpChannelSignalR.Services;

public sealed class StubSessionService(ILogger<StubSessionService> logger) : ISessionService
{
    public Task<string> CreateConversationAsync(string agentId, string topicName, string sender)
    {
        var conversationId = Guid.NewGuid().ToString();
        logger.LogDebug(
            "CreateConversation: agent={AgentId}, topic={TopicName}, sender={Sender}, id={ConversationId}",
            agentId, topicName, sender, conversationId);
        return Task.FromResult(conversationId);
    }
}
