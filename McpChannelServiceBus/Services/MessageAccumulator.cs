using System.Collections.Concurrent;
using System.Text;

namespace McpChannelServiceBus.Services;

public sealed class MessageAccumulator
{
    private readonly ConcurrentDictionary<string, StringBuilder> _buffers = new();

    public void Append(string conversationId, string text)
    {
        _buffers.AddOrUpdate(
            conversationId,
            _ => new StringBuilder(text),
            (_, sb) => sb.Append(text));
    }

    public string? Flush(string conversationId)
    {
        if (!_buffers.TryRemove(conversationId, out var sb) || sb.Length == 0)
        {
            return null;
        }

        return sb.ToString();
    }
}