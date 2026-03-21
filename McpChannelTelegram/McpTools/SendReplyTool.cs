using System.ComponentModel;
using McpChannelTelegram.Services;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace McpChannelTelegram.McpTools;

[McpServerToolType]
public sealed class SendReplyTool
{
    [McpServerTool(Name = "send_reply")]
    [Description("Send a response chunk to a Telegram conversation")]
    public static async Task<string> McpRun(
        [Description("Conversation ID in format chatId:threadId")] string conversationId,
        [Description("Response content")] string content,
        [Description("Content type: text, reasoning, tool_call, error, stream_complete")] string contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        IServiceProvider services)
    {
        var botClient = services.GetRequiredService<ITelegramBotClient>();
        var accumulator = services.GetRequiredService<MessageAccumulator>();
        var (chatId, threadId) = ParseConversationId(conversationId);

        switch (contentType)
        {
            case "reasoning":
                // Telegram doesn't show reasoning — ignore
                return "ok";

            case "tool_call":
                await botClient.SendMessage(
                    chatId,
                    $"\ud83d\udd27 {content}",
                    messageThreadId: threadId,
                    cancellationToken: CancellationToken.None);
                return "ok";

            case "error":
                await SendAccumulatedAsync(botClient, accumulator, conversationId, chatId, threadId);
                await botClient.SendMessage(
                    chatId,
                    $"\u26a0\ufe0f {content}",
                    messageThreadId: threadId,
                    cancellationToken: CancellationToken.None);
                return "ok";

            case "stream_complete":
                await SendAccumulatedAsync(botClient, accumulator, conversationId, chatId, threadId);
                return "ok";

            default:
                // "text" or unknown — accumulate
                accumulator.Append(conversationId, content);

                if (isComplete)
                {
                    await SendAccumulatedAsync(botClient, accumulator, conversationId, chatId, threadId);
                }

                return "ok";
        }
    }

    private static async Task SendAccumulatedAsync(
        ITelegramBotClient botClient,
        MessageAccumulator accumulator,
        string conversationId,
        long chatId,
        int? threadId)
    {
        var chunks = accumulator.Flush(conversationId);
        foreach (var chunk in chunks)
        {
            try
            {
                await botClient.SendMessage(
                    chatId,
                    chunk,
                    ParseMode.Markdown,
                    messageThreadId: threadId,
                    cancellationToken: CancellationToken.None);
            }
            catch
            {
                // Markdown parse failure — retry as plain text
                await botClient.SendMessage(
                    chatId,
                    chunk,
                    messageThreadId: threadId,
                    cancellationToken: CancellationToken.None);
            }
        }
    }

    private static (long ChatId, int? ThreadId) ParseConversationId(string conversationId)
    {
        var parts = conversationId.Split(':');
        var chatId = long.Parse(parts[0]);
        var threadIdVal = long.Parse(parts[1]);

        // If threadId equals chatId, it's a non-forum chat — no thread needed
        return threadIdVal == chatId
            ? (chatId, null)
            : (chatId, Convert.ToInt32(threadIdVal));
    }
}
