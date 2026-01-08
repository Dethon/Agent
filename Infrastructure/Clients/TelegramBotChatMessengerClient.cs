using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Message = Telegram.Bot.Types.Message;

namespace Infrastructure.Clients;

public class TelegramBotChatMessengerClient(
    ITelegramBotClient client,
    string[] allowedUserNames,
    bool showReasoning,
    ILogger<TelegramBotChatMessengerClient> logger) : IChatMessengerClient
{
    private string? _topicIconId;

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int? offset = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            IEnumerable<Message> messageUpdates;
            try
            {
                var updates = await client.GetUpdates(
                    offset: offset,
                    timeout: timeout,
                    cancellationToken: cancellationToken);

                offset = GetNewOffset(updates) ?? offset;

                // Handle callback queries for tool approvals
                foreach (var update in updates.Where(u => u.CallbackQuery is not null))
                {
                    if (update.CallbackQuery is not null)
                    {
                        await TelegramToolApprovalHandler.HandleCallbackQueryAsync(
                            client, update.CallbackQuery, cancellationToken);
                    }
                }

                messageUpdates = updates
                    .Select(u => u.Message)
                    .Where(m => m is not null && m.Type == MessageType.Text && IsBotMessage(m))
                    .Cast<Message>();
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Error))
                {
                    logger.LogError(ex, "Telegram read messages exception: {exceptionMessage}", ex.Message);
                }

                continue;
            }

            foreach (var message in messageUpdates)
            {
                if (!IsAuthorized(message))
                {
                    await client.SendMessage(
                        message.Chat.Id,
                        "You are not authorized to use this bot.",
                        replyParameters: message.Id,
                        cancellationToken: cancellationToken);
                    continue;
                }

                yield return GetPromptFromUpdate(message);

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    public async Task SendResponse(
        long chatId, ChatResponseMessage responseMessage, long? threadId, CancellationToken cancellationToken)
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

    public async Task<int> CreateThread(long chatId, string name, CancellationToken cancellationToken)
    {
        var icon = await GetIcon(cancellationToken);
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
        var icon = await GetIcon(cancellationToken);
        try
        {
            await client.EditForumTopic(
                chatId,
                Convert.ToInt32(threadId),
                iconCustomEmojiId: icon,
                cancellationToken: cancellationToken);
            return true;
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("TOPIC_NOT_MODIFIED"))
        {
            return true;
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("TOPIC_ID_INVALID"))
        {
            return false;
        }
    }

    private async Task<string?> GetIcon(CancellationToken cancellationToken)
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

    private bool IsAuthorized(Message message)
    {
        return allowedUserNames.Contains(message.Chat.Username) ||
               allowedUserNames.Contains(message.From?.Username);
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
}