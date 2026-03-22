namespace McpChannelSignalR.Services;

public interface IStreamService
{
    Task WriteReplyAsync(string conversationId, string content, string contentType, bool isComplete, string? messageId = null);
}
