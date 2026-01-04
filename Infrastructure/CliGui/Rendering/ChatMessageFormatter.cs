using System.Text.Json;
using Domain.DTOs;

namespace Infrastructure.CliGui.Rendering;

internal static class ChatMessageFormatter
{
    private static readonly string[] _lineSeparators = ["\r\n", "\n"];

    public static IEnumerable<ChatLine> FormatMessage(ChatMessage msg)
    {
        var timestamp = msg.Timestamp.ToString("HH:mm");
        var messageLines = msg.Message
            .Split(_lineSeparators, StringSplitOptions.None)
            .SkipWhile(string.IsNullOrWhiteSpace)
            .Reverse()
            .SkipWhile(string.IsNullOrWhiteSpace)
            .Reverse();

        yield return new ChatLine("", ChatLineType.Blank);
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

    public static IEnumerable<ChatLine> FormatReasoning(string reasoning, string groupId)
    {
        var lines = reasoning
            .Split(_lineSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        if (lines.Length == 0)
        {
            yield break;
        }

        yield return new ChatLine(
            $"  ▶ Reasoning ({lines.Length} {(lines.Length == 1 ? "line" : "lines")})",
            ChatLineType.ReasoningHeader,
            groupId,
            IsCollapsible: true);

        foreach (var line in lines)
        {
            yield return new ChatLine(
                $"    │ {line}",
                ChatLineType.ReasoningContent,
                groupId);
        }

        yield return new ChatLine("", ChatLineType.Blank);
    }

    public static IEnumerable<ChatLine> FormatToolResult(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        ToolApprovalResult resultType)
    {
        var (symbol, headerType, contentType) = resultType switch
        {
            ToolApprovalResult.AutoApproved => ("✓", ChatLineType.ToolApprovedHeader, ChatLineType.ToolApprovedContent),
            ToolApprovalResult.Approved => ("✓", ChatLineType.ToolApprovedHeader, ChatLineType.ToolApprovedContent),
            ToolApprovalResult.ApprovedAndRemember => ("✓", ChatLineType.ToolApprovedHeader,
                ChatLineType.ToolApprovedContent),
            ToolApprovalResult.Rejected => ("✗", ChatLineType.ToolRejectedHeader, ChatLineType.ToolRejectedContent),
            _ => ("•", ChatLineType.System, ChatLineType.System)
        };

        yield return new ChatLine($"  {symbol} {toolName.Split(':').Last()}", headerType);

        foreach (var (key, value) in arguments)
        {
            var formattedValue = FormatArgumentValue(value);
            if (formattedValue.Contains('\n'))
            {
                yield return new ChatLine($"    {key}:", contentType);
                foreach (var line in formattedValue.Split(_lineSeparators, StringSplitOptions.None))
                {
                    yield return new ChatLine($"      {line}", contentType);
                }
            }
            else
            {
                yield return new ChatLine($"    {key}: {formattedValue}", contentType);
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
        return items.Count switch
        {
            0 => "[]",
            1 => FormatArgumentValue(items[0]),
            _ => string.Join("\n", items.Select(item => $"- {FormatArgumentValue(item)}"))
        };
    }
}