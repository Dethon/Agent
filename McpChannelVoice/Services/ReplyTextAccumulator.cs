using System.Collections.Concurrent;
using System.Text;

namespace McpChannelVoice.Services;

public sealed class ReplyTextAccumulator
{
    private readonly ConcurrentDictionary<string, StringBuilder> _buffers = new();

    // Keyed by conversation only: a satellite's reply streams as Text chunks that are
    // never marked complete, terminated by a StreamComplete event carrying no messageId.
    // Buffering per-messageId would strand the text under a key the completion can't reach.
    public void Append(string conversationId, string text) =>
        _buffers.AddOrUpdate(conversationId,
            _ => new StringBuilder(text),
            (_, sb) => sb.Append(text));

    public string Flush(string conversationId) =>
        _buffers.TryRemove(conversationId, out var sb) ? sb.ToString() : string.Empty;
}