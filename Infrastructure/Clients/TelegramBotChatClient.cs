using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Infrastructure.Clients;

public class TelegramBotChatClient(ITelegramBotClient client, string[] allowedUserNames) : IChatClient
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
            foreach (var update in updates)
            {
                offset = update.Id + 1;
                if (update.Message?.Text is not null)
                {
                    if (allowedUserNames.Contains(update.Message.Chat.Username) ||
                        allowedUserNames.Contains(update.Message.From?.Username))
                    {
                        if (update.Message.Text.StartsWith('/') || update.Message.MessageThreadId.HasValue)
                        {
                            yield return new ChatPrompt
                            {
                                Prompt = update.Message.Text.TrimStart('/'),
                                ChatId = update.Message.Chat.Id,
                                MessageId = update.Message.MessageId,
                                Sender = update.Message.From?.Username ??
                                         update.Message.Chat.Username ??
                                         update.Message.Chat.FirstName ??
                                         $"{update.Message.Chat.Id}",
                                ReplyToMessageId = update.Message.ReplyToMessage?.MessageId,
                                ThreadId = update.Message.MessageThreadId
                            };
                        }
                    }
                    else
                    {
                        await client.SendMessage(
                            update.Message.Chat.Id,
                            "You are not authorized to use this bot.",
                            replyParameters: update.Message.Id,
                            cancellationToken: cancellationToken);
                    }
                }

                if (cancellationToken.IsCancellationRequested) break;
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
}