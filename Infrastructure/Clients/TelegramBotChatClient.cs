using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace Infrastructure.Clients;

public class TelegramBotChatClient(string token) : IChatClient
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
                    yield return new ChatPrompt
                    {
                        Prompt = update.Message.Text,
                        ChatId = update.Message.Chat.Id,
                        MessageId = update.Message.MessageId,
                    };
                }
                if (cancellationToken.IsCancellationRequested) break;
            }
        }
    }

    public async Task SendResponse(
        long chatId, string response, int? replyId = null, CancellationToken cancellationToken = default)
    {
        var trimmedMessage = response.Length > 4000
            ? $"{response[..4000]} ... (truncated)"
            : response;
        await _botClient.SendMessage(
            chatId, 
            trimmedMessage, 
            parseMode: ParseMode.Html,
            replyParameters: replyId, 
            cancellationToken: cancellationToken);
    }
}