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
                    if (allowedUserNames.Contains(update.Message.Chat.Username))
                    {
                        yield return new ChatPrompt
                        {
                            Prompt = update.Message.Text,
                            ChatId = update.Message.Chat.Id,
                            MessageId = update.Message.MessageId,
                            Sender = update.Message.Chat.Username ??
                                     update.Message.Chat.FirstName ??
                                     $"{update.Message.Chat.Id}",
                            ReplyToMessageId = update.Message.ReplyToMessage?.MessageId
                        };
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
        long chatId, string response, int? replyId = null, CancellationToken cancellationToken = default)
    {
        var message = await client.SendMessage(
            chatId,
            response,
            parseMode: ParseMode.Html,
            replyParameters: replyId,
            cancellationToken: cancellationToken);
        return message.Id;
    }
}