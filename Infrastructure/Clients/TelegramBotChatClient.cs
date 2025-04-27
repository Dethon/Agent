using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs;
using Telegram.Bot;

namespace Infrastructure.Clients;

public class TelegramBotChatClient(string token) : IChatClient
{
    private readonly TelegramBotClient _botClient = new TelegramBotClient(token);
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
                    };
                }
                if (cancellationToken.IsCancellationRequested) break;
            }
        }
    }

    public async Task SendResponse(long chatId, string response, CancellationToken cancellationToken = default)
    {
        await _botClient.SendMessage(chatId, response, cancellationToken: cancellationToken);
    }
}