using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Infrastructure.Clients.Cli;

internal static class ChatHistoryMapper
{
    public static IEnumerable<ChatLine> MapToDisplayLines(
        IReadOnlyList<AiChatMessage> messages,
        string agentName,
        string userName)
    {
        foreach (var message in messages)
        {
            var cliMessage = MapToChatMessage(message, agentName, userName);
            if (cliMessage is null)
            {
                continue;
            }

            foreach (var line in ChatMessageFormatter.FormatMessage(cliMessage))
            {
                yield return line;
            }
        }
    }

    private static ChatMessage? MapToChatMessage(
        AiChatMessage message,
        string agentName,
        string userName)
    {
        var text = message.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return message.Role.Value switch
        {
            "user" => new ChatMessage(userName, text, IsUser: true, IsToolCall: false, IsSystem: false, DateTime.Now),
            "assistant" => new ChatMessage(agentName, text, IsUser: false, IsToolCall: false, IsSystem: false,
                DateTime.Now),
            "system" => new ChatMessage("[System]", text, IsUser: false, IsToolCall: false, IsSystem: true,
                DateTime.Now),
            _ => null
        };
    }
}