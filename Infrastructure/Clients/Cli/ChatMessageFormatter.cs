using System.Text.Json;

namespace Infrastructure.Clients.Cli;

internal static class ChatMessageFormatter
{
    private static readonly string[] LineSeparators = ["\r\n", "\n"];

    public static IEnumerable<ChatLine> FormatMessage(ChatMessage msg)
    {
        var timestamp = msg.Timestamp.ToString("HH:mm");
        var messageLines = msg.Message.Split(LineSeparators, StringSplitOptions.None);

        if (msg.IsSystem)
        {
            foreach (var line in messageLines)
            {
                yield return new ChatLine($"  ○ {line}", ChatLineType.System);
            }
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

    public static IEnumerable<ChatLine> FormatAutoApprovedTool(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments)
    {
        yield return new ChatLine($"  ✓ {toolName}", ChatLineType.AutoApprovedHeader);

        foreach (var (key, value) in arguments)
        {
            var formattedValue = FormatArgumentValue(value);
            if (formattedValue.Contains('\n'))
            {
                yield return new ChatLine($"    {key}:", ChatLineType.AutoApprovedContent);
                foreach (var line in formattedValue.Split(LineSeparators, StringSplitOptions.None))
                {
                    yield return new ChatLine($"      {line}", ChatLineType.AutoApprovedContent);
                }
            }
            else
            {
                yield return new ChatLine($"    {key}: {formattedValue}", ChatLineType.AutoApprovedContent);
            }
        }
    }

    private static string FormatArgumentValue(object? value)
    {
        return value switch
        {
            null => "null",
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString() ?? "",
            JsonElement { ValueKind: JsonValueKind.Array } je => FormatArray(je),
            JsonElement je => je.GetRawText(),
            _ => value.ToString() ?? ""
        };
    }

    private static string FormatArray(JsonElement arrayElement)
    {
        var items = arrayElement.EnumerateArray().ToList();
        if (items.Count == 0)
        {
            return "[]";
        }

        if (items.Count == 1)
        {
            return FormatArgumentValue(items[0]);
        }

        return string.Join("\n", items.Select(item => $"- {FormatArgumentValue(item)}"));
    }
}