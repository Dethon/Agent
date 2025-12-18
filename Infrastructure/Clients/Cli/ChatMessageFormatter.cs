namespace Infrastructure.Clients.Cli;

internal static class ChatMessageFormatter
{
    public static IEnumerable<ChatLine> FormatMessage(ChatMessage msg)
    {
        var timestamp = msg.Timestamp.ToString("HH:mm");
        var messageLines = msg.Message.Split('\n');

        if (msg.IsSystem)
        {
            yield return new ChatLine($"  ○ {msg.Message}", ChatLineType.System);
        }
        else if (msg.IsToolCall)
        {
            yield return new ChatLine("  ┌─ ⚡ Tools ─────────────────", ChatLineType.ToolHeader);
            foreach (var line in messageLines)
            {
                yield return new ChatLine($"  │  {line}", ChatLineType.ToolContent);
            }

            yield return new ChatLine("  └──────────────────────────────", ChatLineType.ToolHeader);
        }
        else if (msg.IsUser)
        {
            yield return new ChatLine($"  ▶ You · {timestamp}", ChatLineType.UserHeader);
            foreach (var line in messageLines)
            {
                yield return new ChatLine($"    {line}", ChatLineType.UserContent);
            }
        }
        else
        {
            yield return new ChatLine($"  ◀ {msg.Sender} · {timestamp}", ChatLineType.AgentHeader);
            foreach (var line in messageLines)
            {
                yield return new ChatLine($"    {line}", ChatLineType.AgentContent);
            }
        }

        yield return new ChatLine("", ChatLineType.Blank);
    }
}