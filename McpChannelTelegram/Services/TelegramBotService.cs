using McpChannelTelegram.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace McpChannelTelegram.Services;

public sealed class TelegramBotService(
    ITelegramBotClient botClient,
    ChannelSettings settings,
    ChannelNotificationEmitter notificationEmitter,
    ApprovalCallbackRouter approvalCallbackRouter,
    ILogger<TelegramBotService> logger) : BackgroundService
{
    private const int PollTimeoutSeconds = 30;

    private int? _offset;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Telegram bot polling started. Allowed usernames: {Usernames}",
            string.Join(", ", settings.AllowedUsernames));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await botClient.GetUpdates(
                    offset: _offset,
                    timeout: PollTimeoutSeconds,
                    cancellationToken: stoppingToken);

                _offset = updates
                    .Select(u => u.Id + 1)
                    .Cast<int?>()
                    .DefaultIfEmpty(null)
                    .Max() ?? _offset;

                foreach (var update in updates)
                {
                    await ProcessUpdateAsync(update, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Telegram polling error: {Message}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        logger.LogInformation("Telegram bot polling stopped");
    }

    private async Task ProcessUpdateAsync(Update update, CancellationToken cancellationToken)
    {
        if (update.CallbackQuery is not null)
        {
            await approvalCallbackRouter.HandleCallbackQueryAsync(botClient, update.CallbackQuery, cancellationToken);
            return;
        }

        if (update.Message is not { Type: MessageType.Text } message || message.Text is null)
        {
            return;
        }

        if (!IsBotMessage(message))
        {
            return;
        }

        var sender = message.From?.Username
                     ?? message.Chat.Username
                     ?? message.Chat.FirstName
                     ?? $"{message.Chat.Id}";

        if (!settings.AllowedUsernames.Contains(sender))
        {
            await botClient.SendMessage(
                message.Chat.Id,
                "You are not authorized to use this bot.",
                replyParameters: message.MessageId,
                cancellationToken: cancellationToken);
            return;
        }

        var chatId = message.Chat.Id;
        var threadId = message.MessageThreadId ?? chatId;
        var conversationId = $"{chatId}:{threadId}";

        if (!notificationEmitter.HasActiveSessions)
        {
            logger.LogWarning("No active MCP sessions, dropping message from {Sender}", sender);
            return;
        }

        await notificationEmitter.EmitMessageNotificationAsync(
            conversationId,
            sender,
            message.Text,
            agentId: "default",
            cancellationToken);

        logger.LogDebug("Emitted message notification for conversation {ConversationId} from {Sender}",
            conversationId, sender);
    }

    private static bool IsBotMessage(Message message)
    {
        return message.Text is not null && (message.Text.StartsWith('/') || message.MessageThreadId.HasValue);
    }
}
