using System.ComponentModel;
using McpChannelTelegram.Services;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace McpChannelTelegram.McpTools;

[McpServerToolType]
public sealed class SubAgentUpdateTool
{
    [McpServerTool(Name = "update_subagent")]
    [Description("Update the status of a previously announced subagent task in Telegram")]
    public static async Task<string> McpRun(
        [Description("Conversation ID in format chatId:threadId")] string conversationId,
        [Description("Unique handle identifying this subagent run")] string handle,
        [Description("New status text (e.g. 'completed', 'failed', 'running')")] string status,
        IServiceProvider services)
    {
        var registry = services.GetRequiredService<BotRegistry>();
        var cardStore = services.GetRequiredService<ISubAgentCardStore>();
        var (chatId, _) = SubAgentAnnounceTool.ParseConversationId(conversationId);
        var botClient = registry.GetBotForChat(chatId)
                        ?? throw new InvalidOperationException($"No bot registered for chat {chatId}");

        if (!cardStore.TryGet(handle, out var card))
        {
            return "not_found";
        }

        var text = $"🤖 <b>Subagent: {card.SubAgentId}</b>\nStatus: {status}";

        await botClient.EditMessageText(
            card.ChatId,
            card.MessageId,
            text,
            ParseMode.Html,
            cancellationToken: CancellationToken.None);

        var isTerminal = !string.Equals(status, "running", StringComparison.OrdinalIgnoreCase);
        if (isTerminal)
        {
            await botClient.EditMessageReplyMarkup(
                card.ChatId,
                card.MessageId,
                replyMarkup: null,
                cancellationToken: CancellationToken.None);

            cardStore.Remove(handle);
        }

        return "updated";
    }
}
