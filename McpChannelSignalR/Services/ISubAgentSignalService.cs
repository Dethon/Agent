namespace McpChannelSignalR.Services;

public interface ISubAgentSignalService
{
    Task AnnounceAsync(string conversationId, string handle, string subAgentId);
    Task UpdateAsync(string conversationId, string handle, string status);
}
