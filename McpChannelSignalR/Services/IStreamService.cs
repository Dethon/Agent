using Domain.DTOs.Channel;

namespace McpChannelSignalR.Services;

public interface IStreamService
{
    Task WriteReplyAsync(SendReplyParams p);
}
