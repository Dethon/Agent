using System.Collections.Concurrent;
using System.Text;

namespace McpChannelVoice.Services;

public sealed class ReplyTextAccumulator
{
    private readonly ConcurrentDictionary<string, StringBuilder> _buffers = new();

    public void Append(string conversationId, string messageId, string text)
    {
        var key = $"{conversationId}|{messageId}";
        _buffers.AddOrUpdate(key,
            _ => new StringBuilder(text),
            (_, sb) => sb.Append(text));
    }

    public string Flush(string conversationId, string messageId)
    {
        var key = $"{conversationId}|{messageId}";
        return _buffers.TryRemove(key, out var sb) ? sb.ToString() : string.Empty;
    }
}