using McpChannelTelegram.Settings;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace McpChannelTelegram.Services;

public sealed class TelegramBotService(
    BotRegistry botRegistry,
    ChannelSettings settings,
    ChannelNotificationEmitter notificationEmitter,
    ApprovalCallbackRouter approvalCallbackRouter,
    ILogger<TelegramBotService> logger) : BackgroundService
{
    private const int PollTimeoutSeconds = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Telegram bot polling started. Allowed usernames: {Usernames}",
            string.Join(", ", settings.AllowedUsernames));

        var pollers = botRegistry.GetAllBots()
            .Select(b => PollBotAsync(b.AgentId, b.Client, stoppingToken))
            .ToArray();

        await Task.WhenAll(pollers);

        logger.LogInformation("Telegram bot polling stopped");
    }

    private async Task PollBotAsync(string agentId, ITelegramBotClient botClient, CancellationToken stoppingToken)
    {
        int? offset = null;

        logger.LogInformation("Started polling for agent {AgentId}", agentId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await botClient.GetUpdates(
                    offset: offset,
                    timeout: PollTimeoutSeconds,
                    cancellationToken: stoppingToken);

                offset = updates
                    .Select(u => u.Id + 1)
                    .Cast<int?>()
                    .DefaultIfEmpty(null)
                    .Max() ?? offset;

                foreach (var update in updates)
                {
                    await ProcessUpdateAsync(agentId, botClient, update, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Telegram polling error for agent {AgentId}: {Message}", agentId, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        logger.LogInformation("Stopped polling for agent {AgentId}", agentId);
    }

    private async Task ProcessUpdateAsync(string agentId, ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
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

        botRegistry.RegisterChatAgent(chatId, agentId);

        // Unlike ServiceBus (broker-level abandon/redeliver) or Schedule/Library (a durable record
        // that simply stays due), Telegram has no channel-level way to signal "try again later" back
        // to the sender — so a stale HasActiveSessions must not gate this away. The stable
        // "channel-telegram" subscriber id survives the agent disconnecting (PruneIdle only evicts
        // an empty, hour-idle subscriber), so buffering here means a late reconnect still delivers
        // the message; dropping it would be silent, permanent loss to a user waiting on a reply.
        if (!notificationEmitter.HasActiveSessions)
        {
            logger.LogWarning("No active MCP sessions; buffering message from {Sender} for later delivery", sender);
        }

        await notificationEmitter.EmitMessageNotificationAsync(
            conversationId,
            sender,
            message.Text,
            agentId: agentId,
            cancellationToken);

        logger.LogDebug("Emitted message notification for conversation {ConversationId} from {Sender} (agent: {AgentId})",
            conversationId, sender, agentId);
    }

    private static bool IsBotMessage(Message message)
    {
        return message.Text is not null && (message.Text.StartsWith('/') || message.MessageThreadId.HasValue);
    }
}