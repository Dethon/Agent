using Microsoft.Extensions.Logging;

namespace McpChannelSignalR.Services;

public sealed class StubStreamService(ILogger<StubStreamService> logger) : IStreamService
{
    public Task WriteReplyAsync(string conversationId, string content, string contentType, bool isComplete)
    {
        logger.LogDebug(
            "WriteReply: conversation={ConversationId}, type={ContentType}, complete={IsComplete}, length={Length}",
            conversationId, contentType, isComplete, content.Length);
        return Task.CompletedTask;
    }
}
