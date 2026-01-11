using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Message = Telegram.Bot.Types.Message;

namespace Infrastructure.Clients;

public class TelegramChatClient(
    IEnumerable<string> botTokens,
    string[] allowedUserNames,
    bool showReasoning,
    ILogger<TelegramChatClient> logger,
    string? baseUrl = null) : IChatMessengerClient
{
    private readonly Dictionary<string, BotContext> _bots = botTokens
        .Select(token => new BotContext(token, CreateBotClient(token, baseUrl)))
        .ToDictionary(ctx => ctx.TokenHash, ctx => ctx);

    private static TelegramBotClient CreateBotClient(string token, string? baseUrl)
    {
        if (baseUrl is null)
        {
            return new TelegramBotClient(token);
        }

        var options = new TelegramBotClientOptions(token, baseUrl);
        return new TelegramBotClient(options);
    }

    private string? _topicIconId;

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pendingTasks = _bots.Values
                .Select(bot => PollBotAsync(bot, timeout, cancellationToken))
                .ToList();

            while (pendingTasks.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                var completedTask = await Task.WhenAny(pendingTasks);
                pendingTasks.Remove(completedTask);

                var messages = await completedTask;
                foreach (var (prompt, client, tokenHash) in messages)
                {
                    if (!IsAuthorized(prompt))
                    {
                        await client.SendMessage(
                            prompt.ChatId,
                            "You are not authorized to use this bot.",
                            replyParameters: prompt.MessageId,
                            cancellationToken: cancellationToken);
                        continue;
                    }

                    yield return prompt with { BotTokenHash = tokenHash };
                }
            }
        }
    }

    public async Task SendResponse(
        long chatId, ChatResponseMessage responseMessage, long? threadId, CancellationToken cancellationToken)
    {
        // Use the first bot for sending responses in single-bot scenarios.
        // In multi-bot scenarios, we'd need to track which bot to use per chat.
        var client = _bots.Values.First().Client;
        await SendResponseWithClient(client, chatId, responseMessage, threadId, cancellationToken);
    }

    public async Task<int> CreateThread(long chatId, string name, CancellationToken cancellationToken)
    {
        var client = _bots.Values.First().Client;
        var icon = await GetIcon(client, cancellationToken);
        var thread = await client.CreateForumTopic(
            chatId,
            name,
            iconCustomEmojiId: icon,
            cancellationToken: cancellationToken
        );
        return thread.MessageThreadId;
    }

    public async Task<bool> DoesThreadExist(long chatId, long threadId, CancellationToken cancellationToken)
    {
        var client = _bots.Values.First().Client;
        var icon = await GetIcon(client, cancellationToken);
        try
        {
            await client.EditForumTopic(
                chatId,
                Convert.ToInt32(threadId),
                iconCustomEmojiId: icon,
                cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex.Message.Contains("TOPIC_NOT_MODIFIED"))
        {
            return true;
        }
        catch (Exception ex) when (ex.Message.Contains("TOPIC_ID_INVALID"))
        {
            return false;
        }
    }

    private async Task<List<(ChatPrompt Prompt, ITelegramBotClient Client, string TokenHash)>> PollBotAsync(
        BotContext bot, int timeout, CancellationToken cancellationToken)
    {
        var results = new List<(ChatPrompt, ITelegramBotClient, string)>();

        try
        {
            var updates = await bot.Client.GetUpdates(
                offset: bot.Offset,
                timeout: timeout,
                cancellationToken: cancellationToken);

            bot.Offset = GetNewOffset(updates) ?? bot.Offset;

            // Handle callback queries for tool approvals
            foreach (var update in updates.Where(u => u.CallbackQuery is not null))
            {
                if (update.CallbackQuery is not null)
                {
                    await TelegramToolApprovalHandler.HandleCallbackQueryAsync(
                        bot.Client, update.CallbackQuery, cancellationToken);
                }
            }

            var messageUpdates = updates
                .Select(u => u.Message)
                .Where(m => m is not null && m.Type == MessageType.Text && IsBotMessage(m))
                .Cast<Message>();

            results
                .AddRange(messageUpdates
                    .Select(GetPromptFromUpdate)
                    .Select(prompt => (prompt, bot.Client, bot.TokenHash)));
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError(ex, "Telegram read messages exception for bot {TokenHash}: {ExceptionMessage}",
                    bot.TokenHash[..8], ex.Message);
            }
        }

        return results;
    }

    private async Task SendResponseWithClient(
        ITelegramBotClient client,
        long chatId,
        ChatResponseMessage responseMessage,
        long? threadId,
        CancellationToken cancellationToken)
    {
        var toolCalls = responseMessage.CalledTools?.HtmlSanitize().Left(3800);
        var content = responseMessage.Message?.HtmlSanitize().Left(4000);
        var reasoning = showReasoning ? responseMessage.Reasoning?.HtmlSanitize().Left(4000) : null;

        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            await client.SendMessage(
                chatId,
                $"<blockquote expandable>{reasoning}</blockquote>",
                ParseMode.Html,
                messageThreadId: Convert.ToInt32(threadId),
                cancellationToken: cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(content))
        {
            await client.SendMessage(
                chatId,
                responseMessage.Bold ? $"<b>{content}</b>" : content,
                ParseMode.Html,
                messageThreadId: Convert.ToInt32(threadId),
                cancellationToken: cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(toolCalls))
        {
            var toolMessage = "<blockquote expandable>" +
                              $"<pre><code class=\"language-json\">{toolCalls}</code></pre>" +
                              "</blockquote>";
            await client.SendMessage(
                chatId,
                toolMessage,
                ParseMode.Html,
                messageThreadId: Convert.ToInt32(threadId),
                cancellationToken: cancellationToken);
        }
    }

    private async Task<string?> GetIcon(ITelegramBotClient client, CancellationToken cancellationToken)
    {
        if (_topicIconId is not null)
        {
            return _topicIconId;
        }

        var icons = await client.GetForumTopicIconStickers(cancellationToken);
        var icon = icons.FirstOrDefault(x => x.Emoji == "🏴‍☠️");
        _topicIconId = icon?.CustomEmojiId;
        return _topicIconId;
    }

    private bool IsAuthorized(ChatPrompt prompt)
    {
        return allowedUserNames.Contains(prompt.Sender);
    }

    private static bool IsBotMessage(Message message)
    {
        return message.Text is not null && (message.Text.StartsWith('/') || message.MessageThreadId.HasValue);
    }

    private static int? GetNewOffset(Update[] updates)
    {
        return updates.Select(u => u.Id + 1).Cast<int?>().DefaultIfEmpty(null).Max();
    }

    private static ChatPrompt GetPromptFromUpdate(Message message)
    {
        if (message.Text is null)
        {
            throw new ArgumentException(nameof(message.Text));
        }

        return new ChatPrompt
        {
            Prompt = message.Text,
            ChatId = message.Chat.Id,
            MessageId = message.MessageId,
            Sender = message.From?.Username ??
                     message.Chat.Username ??
                     message.Chat.FirstName ??
                     $"{message.Chat.Id}",
            ThreadId = message.MessageThreadId
        };
    }

    private sealed class BotContext(string token, ITelegramBotClient client)
    {
        public ITelegramBotClient Client { get; } = client;
        public string TokenHash { get; } = ComputeHash(token);
        public int? Offset { get; set; }

        private static string ComputeHash(string token)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return Convert.ToHexStringLower(bytes);
        }
    }
}
