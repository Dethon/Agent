using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Message = Telegram.Bot.Types.Message;

namespace Infrastructure.Clients;

public class TelegramBotChatMessengerClient(ITelegramBotClient client, string[] allowedUserNames) : IChatMessengerClient
{
    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int? offset = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var updates = await client.GetUpdates(
                offset: offset,
                timeout: timeout,
                cancellationToken: cancellationToken);

            offset = GetNewOffset(updates) ?? offset;
            var messageUpdates = updates
                .Select(u => u.Message)
                .Where(m => m is not null && m.Type == MessageType.Text && IsBotMessage(m))
                .Cast<Message>();

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

    public async Task<int> SendResponse(
        long chatId, string response, int? messageThreadId = null, CancellationToken cancellationToken = default)
    {
        var message = await client.SendMessage(
            chatId,
            response,
            parseMode: ParseMode.Html,
            messageThreadId: messageThreadId,
            cancellationToken: cancellationToken);
        return message.Id;
    }

    public async Task<int> CreateThread(long chatId, string name, CancellationToken cancellationToken = default)
    {
        var icons = await client.GetForumTopicIconStickers(cancellationToken);
        var thread = await client.CreateForumTopic(
            chatId,
            name,
            iconCustomEmojiId: icons.FirstOrDefault(x => x.Emoji == "🏴‍☠️")?.CustomEmojiId,
            cancellationToken: cancellationToken
        );
        return thread.MessageThreadId;
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
            Prompt = message.Text.TrimStart('/'),
            IsCommand = message.Text.StartsWith('/'),
            ChatId = message.Chat.Id,
            MessageId = message.MessageId,
            Sender = message.From?.Username ??
                     message.Chat.Username ??
                     message.Chat.FirstName ??
                     $"{message.Chat.Id}",
            ReplyToMessageId = message.ReplyToMessage?.MessageId,
            ThreadId = message.MessageThreadId
        };
    }
}