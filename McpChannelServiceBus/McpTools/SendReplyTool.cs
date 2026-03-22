using System.ComponentModel;
using McpChannelServiceBus.Services;
using ModelContextProtocol.Server;

namespace McpChannelServiceBus.McpTools;

[McpServerToolType]
public sealed class SendReplyTool
{
    [McpServerTool(Name = "send_reply")]
    [Description("Send a response chunk to a Service Bus conversation")]
    public static async Task<string> McpRun(
        [Description("Conversation ID (correlationId)")] string conversationId,
        [Description("Response content")] string content,
        [Description("Content type: text, reasoning, tool_call, error, stream_complete")] string contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        [Description("Message ID for grouping related chunks")] string? messageId,
        IServiceProvider services)
    {
        var accumulator = services.GetRequiredService<MessageAccumulator>();
        var responseSender = services.GetRequiredService<ResponseSender>();

        switch (contentType)
        {
            case "reasoning":
            case "tool_call":
                // ServiceBus doesn't need intermediate updates
                return "ok";

            case "error":
                var errorContent = accumulator.Flush(conversationId);
                var errorMessage = string.IsNullOrEmpty(errorContent)
                    ? content
                    : $"{errorContent}\n\nError: {content}";
                await responseSender.SendResponseAsync(conversationId, errorMessage);
                return "ok";

            case "stream_complete":
                var accumulated = accumulator.Flush(conversationId);
                if (!string.IsNullOrEmpty(accumulated))
                {
                    await responseSender.SendResponseAsync(conversationId, accumulated);
                }
                return "ok";

            default:
                // "text" or unknown — accumulate
                accumulator.Append(conversationId, content);

                if (isComplete)
                {
                    var flushed = accumulator.Flush(conversationId);
                    if (!string.IsNullOrEmpty(flushed))
                    {
                        await responseSender.SendResponseAsync(conversationId, flushed);
                    }
                }

                return "ok";
        }
    }
}
