using System.ComponentModel;
using McpChannelSignalR.Services;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class SendReplyTool
{
    [McpServerTool(Name = "send_reply")]
    [Description("Send a response chunk to a WebChat conversation")]
    public static async Task<string> McpRun(
        [Description("Conversation ID")] string conversationId,
        [Description("Response content")] string content,
        [Description("Content type: text, reasoning, tool_call, error, stream_complete")] string contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        [Description("Message ID for grouping related chunks into bubbles")] string? messageId,
        IServiceProvider services)
    {
        var streamService = services.GetRequiredService<IStreamService>();
        await streamService.WriteReplyAsync(conversationId, content, contentType, isComplete, messageId);
        return "ok";
    }
}
