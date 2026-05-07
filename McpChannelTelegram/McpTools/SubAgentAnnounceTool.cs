using System.ComponentModel;
using McpChannelTelegram.Services;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace McpChannelTelegram.McpTools;

[McpServerToolType]
public sealed class SubAgentAnnounceTool
{
    private const string CancelCallbackPrefix = "subagent_cancel:";

    [McpServerTool(Name = "announce_subagent")]
    [Description("Announce a subagent task to the user via Telegram with a Cancel button")]
    public static async Task<string> McpRun(
        [Description("Conversation ID in format chatId:threadId")] string conversationId,
        [Description("Unique handle identifying this subagent run")] string handle,
        [Description("Subagent identifier (name/type)")] string subAgentId,
        IServiceProvider services)
    {
        var registry = services.GetRequiredService<BotRegistry>();
        var cardStore = services.GetRequiredService<ISubAgentCardStore>();
        var (chatId, threadId) = ParseConversationId(conversationId);
        var botClient = registry.GetBotForChat(chatId)
                        ?? throw new InvalidOperationException($"No bot registered for chat {chatId}");

        var text = $"🤖 <b>Subagent: {subAgentId}</b>\nStatus: Running";
        var keyboard = new InlineKeyboardMarkup([[
            InlineKeyboardButton.WithCallbackData("❌ Cancel", $"{CancelCallbackPrefix}{handle}")
        ]]);

        var message = await botClient.SendMessage(
            chatId,
            text,
            ParseMode.Html,
            replyMarkup: keyboard,
            messageThreadId: threadId,
            cancellationToken: CancellationToken.None);

        cardStore.Track(handle, chatId, message.MessageId, subAgentId);

        return "announced";
    }

    internal static (long ChatId, int? ThreadId) ParseConversationId(string conversationId)
    {
        var parts = conversationId.Split(':');
        var chatId = long.Parse(parts[0]);
        var threadIdVal = long.Parse(parts[1]);

        return threadIdVal == chatId
            ? (chatId, null)
            : (chatId, Convert.ToInt32(threadIdVal));
    }
}
