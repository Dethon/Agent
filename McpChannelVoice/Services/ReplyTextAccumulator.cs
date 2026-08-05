using System.Collections.Concurrent;
using System.Text;
using McpChannelVoice.Services.Tts;

namespace McpChannelVoice.Services;

public sealed class ReplyTextAccumulator
{
    private readonly ConcurrentDictionary<string, StringBuilder> _buffers = new();

    // Keyed by conversation only: a satellite's reply streams as Text chunks that are
    // never marked complete, terminated by a StreamComplete event carrying no messageId.
    // Buffering per-messageId would strand the text under a key the completion can't reach.
    public void Append(string conversationId, string text)
    {
        var buffer = _buffers.GetOrAdd(conversationId, _ => new StringBuilder());
        lock (buffer)
        {
            buffer.Append(text);
        }
    }

    // Takes the largest complete sentence run currently buffered and leaves the partial tail behind,
    // so the hub can speak it while the agent is still generating. False means the tail holds no
    // boundary past minChars — keep buffering.
    //
    // Ordering is the caller's, not this lock's: ChatMonitor's reply dispatcher awaits each
    // send_reply before the next, so one conversation's chunks arrive strictly sequentially and
    // take-then-enqueue preserves playback order without further sequencing here. The lock guards
    // the buffer itself, which StringBuilder does not do.
    public bool TryTakeSpeakable(string conversationId, int minChars, out string speakable)
    {
        speakable = string.Empty;
        if (!_buffers.TryGetValue(conversationId, out var buffer))
        {
            return false;
        }

        lock (buffer)
        {
            if (!SentenceSplitter.TryTake(buffer.ToString(), minChars, out var taken, out var remainder))
            {
                return false;
            }

            buffer.Clear();
            buffer.Append(remainder);
            speakable = taken;
            return true;
        }
    }

    // Hands a taken run back, for a segment the playback queue refused after TryTakeSpeakable had
    // already removed it from the buffer. Prepended rather than appended: the run is older than
    // anything that arrived while it was in flight, and the answer is spoken in the order it was
    // written.
    public void PutBack(string conversationId, string text)
    {
        var buffer = _buffers.GetOrAdd(conversationId, _ => new StringBuilder());
        lock (buffer)
        {
            buffer.Insert(0, text);
        }
    }

    public string Flush(string conversationId)
    {
        if (!_buffers.TryRemove(conversationId, out var buffer))
        {
            return string.Empty;
        }

        lock (buffer)
        {
            return buffer.ToString();
        }
    }
}