using Domain.DTOs.Channel;

namespace McpChannelSignalR.Services;

public interface ISessionService
{
    Task<string> CreateConversationAsync(CreateConversationParams p);
}
