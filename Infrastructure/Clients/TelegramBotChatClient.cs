using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Infrastructure.Clients;

public class TelegramBotChatClient(string token, string[] allowedUserNames) : IChatClient
{
    private readonly TelegramBotClient _botClient = new(token);

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int? offset = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var updates = await _botClient.GetUpdates(
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
                            ReplyToMessageId = update.Message.ReplyToMessage?.MessageId
                        };
                    }
                    else
                    {
                        await _botClient.SendMessage(
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
        var trimmedMessage = response.Length > 4050
            ? $"{response[..4050]} ... (truncated)"
            : response;
        var message = await _botClient.SendMessage(
            chatId,
            trimmedMessage,
            parseMode: ParseMode.Html,
            replyParameters: replyId,
            cancellationToken: cancellationToken);
        return message.Id;
    }
}