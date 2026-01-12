using System.Security.Cryptography;
using System.Text;
using Telegram.Bot;

namespace Infrastructure.Clients;

public static class TelegramBotHelper
{
    public static string ComputeTokenHash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(bytes);
    }

    public static Dictionary<string, ITelegramBotClient> CreateBotClientsByHash(
        IEnumerable<string> botTokens,
        string? baseUrl = null)
    {
        return botTokens.ToDictionary(
            ComputeTokenHash, ITelegramBotClient (token) => CreateBotClient(token, baseUrl));
    }

    public static ITelegramBotClient GetClientByHash(
        IReadOnlyDictionary<string, ITelegramBotClient> botsByHash,
        string? botTokenHash)
    {
        ArgumentNullException.ThrowIfNull(botTokenHash);
        return botsByHash.TryGetValue(botTokenHash, out var client)
            ? client
            : throw new ArgumentException("Invalid bot token hash", nameof(botTokenHash));
    }

    private static TelegramBotClient CreateBotClient(string token, string? baseUrl)
    {
        if (baseUrl is null)
        {
            return new TelegramBotClient(token);
        }

        var options = new TelegramBotClientOptions(token, baseUrl);
        return new TelegramBotClient(options);
    }
}