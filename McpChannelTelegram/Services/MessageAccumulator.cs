using System.Collections.Concurrent;
using System.Text;

namespace McpChannelTelegram.Services;

public sealed class MessageAccumulator
{
    private const int TelegramMessageLimit = 4096;

    private readonly ConcurrentDictionary<string, StringBuilder> _buffers = new();

    public void Append(string conversationId, string text)
    {
        _buffers.AddOrUpdate(
            conversationId,
            _ => new StringBuilder(text),
            (_, sb) => sb.Append(text));
    }

    public IReadOnlyList<string> Flush(string conversationId)
    {
        if (!_buffers.TryRemove(conversationId, out var sb) || sb.Length == 0)
        {
            return [];
        }

        var fullText = sb.ToString();

        return fullText.Length <= TelegramMessageLimit
            ? [fullText]
            : SplitMessage(fullText);
    }

    private static List<string> SplitMessage(string text)
    {
        var chunks = new List<string>();
        var remaining = text.AsSpan();

        while (remaining.Length > 0)
        {
            if (remaining.Length <= TelegramMessageLimit)
            {
                chunks.Add(remaining.ToString());
                break;
            }

            // Try to split at a newline boundary within the limit
            var slice = remaining[..TelegramMessageLimit];
            var splitIndex = slice.LastIndexOf('\n');

            if (splitIndex <= 0)
            {
                splitIndex = TelegramMessageLimit;
            }

            chunks.Add(remaining[..splitIndex].ToString());
            remaining = remaining[splitIndex..].TrimStart('\n');
        }

        return chunks;
    }
}