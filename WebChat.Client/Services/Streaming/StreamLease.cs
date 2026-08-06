using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.Services.Streaming;

// What the opener of a topic stream holds. It is the only way to add to that stream or end it,
// and its own identity is the stream's identity: once the topic has moved on, this lease can do
// nothing at all. That is what makes a finishing stream unable to disturb the newer one that
// replaced it — there is no comparison for a caller to forget to write.
public sealed class StreamLease
{
    private readonly TopicStreams _streams;
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal StreamLease(TopicStreams streams, string topicId)
    {
        _streams = streams;
        TopicId = topicId;
    }

    public string TopicId { get; }

    // Finishes when this topic stream ends, whichever way it ended: the reply finishing, the
    // stop button, or the topic being deleted.
    public Task Completion => _ended.Task;

    public string? CurrentMessageId => _streams.CurrentMessageIdOf(this);

    public StreamAppend Append(ChatStreamMessage chunk) => _streams.Append(this, chunk);

    public StreamAppend AppendToCommittedMessage(ChatStreamMessage chunk) =>
        _streams.AppendToCommittedMessage(this, chunk);

    public ChatMessageModel? StartMessage(string? messageId, ChatMessageModel? resume = null) =>
        _streams.StartMessage(this, messageId, resume);

    public void Complete() => _streams.Complete(this);

    internal void MarkEnded() => _ended.TrySetResult();
}