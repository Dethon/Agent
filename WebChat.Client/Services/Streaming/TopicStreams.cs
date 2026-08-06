using Domain.DTOs.WebChat;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;

namespace WebChat.Client.Services.Streaming;

public enum TopicStreamPhase
{
    None,
    Resuming,
    Streaming
}

// What a topic's stream currently is, for a caller that needs to ask rather than write.
// Stream is the loop pulling chunks off the wire; Completion finishes when the topic stream
// ends, which is the earlier of the two when the stop button or a delete ended it.
public sealed record TopicStreamSnapshot(
    TopicStreamPhase Phase,
    Task? Stream,
    Task? Completion,
    ChatMessageModel? Message,
    string? CurrentMessageId)
{
    public static TopicStreamSnapshot None { get; } = new(TopicStreamPhase.None, null, null, null, null);

    public bool HasStream => Phase is not TopicStreamPhase.None;

    public bool IsResuming => Phase is TopicStreamPhase.Resuming;

    public bool IsStreaming => Phase is TopicStreamPhase.Streaming;
}

// What appending gives back: the assistant message accumulated so far, and whether this chunk
// added anything to it. A caller that keeps its own copy of either is keeping a second truth.
public readonly record struct StreamAppend(ChatMessageModel Message, bool IsNew)
{
    public static StreamAppend Nothing { get; } = new(new ChatMessageModel { Role = "assistant" }, false);
}

// A topic stream is a topic's one reply in flight, from the send or resume that opened it to
// its single ending. This module holds one record per topic and is the only thing that moves a
// topic between having no stream, resuming and streaming. It is also the only writer of the
// streaming slice of state, which is the projection it publishes for rendering — see
// docs/adr/0017.
public sealed class TopicStreams(IDispatcher dispatcher, MessagesStore messagesStore)
{
    private readonly Dictionary<string, TopicStream> _byTopic = [];
    private readonly Lock _lock = new();

    // Null means the topic already has a stream. The caller then holds nothing, and holding
    // nothing is the only way to be unable to open a second reply over a live one.
    public StreamLease? TryOpen(
        string topicId,
        ChatMessageModel message,
        string? currentMessageId,
        Func<StreamLease, Task> run)
    {
        StreamLease lease;
        lock (_lock)
        {
            if (_byTopic.ContainsKey(topicId))
            {
                return null;
            }

            lease = new StreamLease(this, topicId);
            _byTopic[topicId] = new TopicStream(lease)
            {
                Phase = TopicStreamPhase.Streaming,
                Message = message,
                CurrentMessageId = currentMessageId
            };
        }

        // StreamStarted resets the buffer, so it goes out before the first chunk can arrive.
        dispatcher.Dispatch(new StreamStarted(topicId));
        Attach(lease, run);
        return lease;
    }

    // A resume claims the topic before it knows whether there is anything to resume, so two
    // reconnects in a row cannot both decide to resume the same reply.
    public StreamLease? TryBeginResume(string topicId)
    {
        lock (_lock)
        {
            if (_byTopic.ContainsKey(topicId))
            {
                return null;
            }

            var lease = new StreamLease(this, topicId);
            _byTopic[topicId] = new TopicStream(lease) { Phase = TopicStreamPhase.Resuming };
            return lease;
        }
    }

    // The resume found a reply in progress: the same record becomes the stream, in place.
    public bool TryStream(
        StreamLease lease,
        ChatMessageModel message,
        string? currentMessageId,
        Func<StreamLease, Task> run)
    {
        lock (_lock)
        {
            if (Held(lease) is not { Phase: TopicStreamPhase.Resuming } stream)
            {
                return false;
            }

            stream.Phase = TopicStreamPhase.Streaming;
            stream.Message = message;
            stream.CurrentMessageId = currentMessageId;
        }

        dispatcher.Dispatch(new StreamStarted(lease.TopicId));
        Attach(lease, run);
        return true;
    }

    public TopicStreamSnapshot Snapshot(string topicId)
    {
        lock (_lock)
        {
            return _byTopic.TryGetValue(topicId, out var stream)
                ? new TopicStreamSnapshot(
                    stream.Phase,
                    stream.Stream,
                    stream.Lease.Completion,
                    stream.Message,
                    stream.CurrentMessageId)
                : TopicStreamSnapshot.None;
        }
    }

    // The three verbs below are for callers that legitimately touch a topic stream without
    // having opened it: a tool call finishing, an approval resolved, another person's message,
    // the stop button, a topic being deleted. Each does nothing on a topic with no reply in
    // flight, which is where "a chunk for an idle topic" is answered, once.

    public void Append(string topicId, ChatStreamMessage chunk)
    {
        Grown grown;
        lock (_lock)
        {
            grown = Grow(Streaming(topicId), chunk);
        }

        Publish(topicId, grown);
    }

    // Shows the accumulator a resume rebuilt in the live buffer. Nothing to publish on a topic
    // with no reply in flight, and nothing new to publish on one that has written nothing.
    public void PublishCurrent(string topicId)
    {
        ChatMessageModel message;
        string? messageId;
        lock (_lock)
        {
            if (Streaming(topicId) is not { Message.HasContent: true } stream)
            {
                return;
            }

            message = stream.Message;
            messageId = stream.CurrentMessageId;
        }

        Publish(topicId, new Grown(new StreamAppend(message, true), messageId));
    }

    public void FinalizeCurrent(string topicId)
    {
        ChatMessageModel finished;
        string? messageId;
        lock (_lock)
        {
            if (Streaming(topicId) is not { Message.HasContent: true } stream)
            {
                return;
            }

            finished = stream.Message;
            messageId = stream.CurrentMessageId;
            stream.Message = new ChatMessageModel { Role = "assistant" };
        }

        Commit(topicId, finished, messageId);
        dispatcher.Dispatch(new ResetStreamingContent(topicId));
    }

    public void End(string topicId)
    {
        TopicStream? stream;
        lock (_lock)
        {
            stream = _byTopic.GetValueOrDefault(topicId);
            _byTopic.Remove(topicId);
        }

        Close(stream);
    }

    internal StreamAppend Append(StreamLease lease, ChatStreamMessage chunk)
    {
        Grown grown;
        lock (_lock)
        {
            grown = Grow(Streaming(Held(lease)), chunk);
        }

        Publish(lease.TopicId, grown);
        return grown.Append;
    }

    // The current message already has a bubble of its own, so the accumulation stays out of the
    // live buffer and the caller updates that bubble instead. Two live copies of one message is
    // what the single-live-bubble look exists to avoid.
    internal StreamAppend AppendToCommittedMessage(StreamLease lease, ChatStreamMessage chunk)
    {
        lock (_lock)
        {
            return Grow(Streaming(Held(lease)), chunk).Append;
        }
    }

    // A turn boundary. Returns the message this stream was writing, so a caller that wants to
    // come back to it can keep it; null when the lease is stale or there was nothing to commit.
    internal ChatMessageModel? StartMessage(StreamLease lease, string? messageId, ChatMessageModel? resume)
    {
        ChatMessageModel? finished;
        string? finishedId;
        lock (_lock)
        {
            if (Streaming(Held(lease)) is not { } stream)
            {
                return null;
            }

            finished = stream.Message.HasContent ? stream.Message : null;
            finishedId = stream.CurrentMessageId;
            stream.Message = resume ?? new ChatMessageModel { Role = "assistant" };
            stream.CurrentMessageId = messageId;
        }

        if (finished is null)
        {
            return null;
        }

        Commit(lease.TopicId, finished, finishedId);
        dispatcher.Dispatch(new ResetStreamingContent(lease.TopicId));
        return finished;
    }

    internal void Complete(StreamLease lease)
    {
        TopicStream? stream;
        lock (_lock)
        {
            stream = Held(lease);
            if (stream is not null)
            {
                _byTopic.Remove(lease.TopicId);
            }
        }

        Close(stream);
    }

    internal string? CurrentMessageIdOf(StreamLease lease)
    {
        lock (_lock)
        {
            return Held(lease)?.CurrentMessageId;
        }
    }

    private void Attach(StreamLease lease, Func<StreamLease, Task> run)
    {
        var task = run(lease);
        lock (_lock)
        {
            var stream = Held(lease);
            if (stream is not null)
            {
                stream.Stream = task;
            }
        }
    }

    // Ending is one path whichever way it was reached: whatever the reply had written is kept
    // as a message, the topic goes back to having no stream, and the lease that opened it can
    // no longer do anything.
    private void Close(TopicStream? stream)
    {
        if (stream is null)
        {
            return;
        }

        if (stream.Phase is TopicStreamPhase.Streaming)
        {
            Commit(stream.Lease.TopicId, stream.Message, stream.CurrentMessageId);
            dispatcher.Dispatch(new StreamCompleted(stream.Lease.TopicId));
        }

        stream.Lease.MarkEnded();
    }

    private static Grown Grow(TopicStream? stream, ChatStreamMessage chunk)
    {
        if (stream is null)
        {
            return new Grown(StreamAppend.Nothing, null);
        }

        var before = stream.Message;
        var after = BufferRebuildUtility.AccumulateChunk(before, chunk);

        stream.Message = after;
        if (chunk.MessageId is not null)
        {
            stream.CurrentMessageId = chunk.MessageId;
        }

        var isNew =
            after.Content.Length > before.Content.Length ||
            (after.Reasoning?.Length ?? 0) > (before.Reasoning?.Length ?? 0) ||
            (after.ToolCalls?.Length ?? 0) > (before.ToolCalls?.Length ?? 0);

        return new Grown(new StreamAppend(after, isNew), stream.CurrentMessageId);
    }

    private void Publish(string topicId, Grown grown)
    {
        if (!grown.Append.IsNew)
        {
            return;
        }

        var message = grown.Append.Message;
        dispatcher.Dispatch(new StreamChunk(
            topicId, message.Content, message.Reasoning, message.ToolCalls, grown.MessageId));
    }

    private void Commit(string topicId, ChatMessageModel message, string? messageId)
    {
        if (!message.HasContent)
        {
            return;
        }

        var finalized = messageId is not null &&
                        messagesStore.State.FinalizedMessageIdsByTopic
                            .GetValueOrDefault(topicId)?.Contains(messageId) == true;

        if (finalized)
        {
            dispatcher.Dispatch(new UpdateMessage(topicId, messageId!, message));
            return;
        }

        // AddMessage records the id, so the next read sees the message as committed.
        dispatcher.Dispatch(new AddMessage(topicId, message, messageId));
    }

    private TopicStream? Held(StreamLease lease) =>
        _byTopic.TryGetValue(lease.TopicId, out var stream) && ReferenceEquals(stream.Lease, lease)
            ? stream
            : null;

    private TopicStream? Streaming(string topicId) => Streaming(_byTopic.GetValueOrDefault(topicId));

    private static TopicStream? Streaming(TopicStream? stream) =>
        stream is { Phase: TopicStreamPhase.Streaming } ? stream : null;

    private readonly record struct Grown(StreamAppend Append, string? MessageId);

    private sealed class TopicStream(StreamLease lease)
    {
        public StreamLease Lease { get; } = lease;

        public TopicStreamPhase Phase { get; set; }

        public Task? Stream { get; set; }

        public ChatMessageModel Message { get; set; } = new() { Role = "assistant" };

        public string? CurrentMessageId { get; set; }
    }
}