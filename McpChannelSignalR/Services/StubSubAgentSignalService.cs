namespace McpChannelSignalR.Services;

public sealed class StubSubAgentSignalService(ILogger<StubSubAgentSignalService> logger) : ISubAgentSignalService
{
    public Task AnnounceAsync(string conversationId, string handle, string subAgentId)
    {
        logger.LogDebug(
            "SubAgentAnnounce: conversation={ConversationId}, handle={Handle}, subAgentId={SubAgentId}",
            conversationId, handle, subAgentId);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(string conversationId, string handle, string status)
    {
        logger.LogDebug(
            "SubAgentUpdate: conversation={ConversationId}, handle={Handle}, status={Status}",
            conversationId, handle, status);
        return Task.CompletedTask;
    }
}
