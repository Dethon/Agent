using Telegram.Bot;

namespace Infrastructure.Clients.Messaging.Telegram;

public static class TelegramBotHelper
{
    public static TelegramBotClient CreateBotClient(string token, string? baseUrl = null)
    {
        if (baseUrl is null)
        {
            return new TelegramBotClient(token);
        }

        var options = new TelegramBotClientOptions(token, baseUrl);
        return new TelegramBotClient(options);
    }
}