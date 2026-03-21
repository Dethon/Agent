namespace McpChannelSignalR.Services;

public interface ISessionService
{
    Task<string> CreateConversationAsync(string agentId, string topicName, string sender);
}
