using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class SendReplyTool
{
    [McpServerTool(Name = ChannelProtocol.SendReplyTool)]
    [Description("Speak a response chunk on the originating voice satellite")]
    public static Task<string> McpRun(
        [Description("Satellite ID owning the conversation")] string conversationId,
        [Description("Response content")] string content,
        [Description("Kind of chunk being sent")] ReplyContentType contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        [Description("Message ID for grouping related chunks")] string? messageId,
        IServiceProvider services)
    {
        var logger = services.GetService<ILogger<SendReplyTool>>();
        logger?.LogInformation(
            "send_reply (placeholder) conversation={ConversationId} type={ContentType} complete={IsComplete}",
            conversationId, contentType, isComplete);
        return Task.FromResult("ok");
    }
}