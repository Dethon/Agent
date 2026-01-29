using System.Reactive.Subjects;
using Domain.DTOs.WebChat;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;

namespace WebChat.Client.State.Pipeline;

public sealed class MessagePipeline(
    IDispatcher dispatcher,
    MessagesStore messagesStore,
    StreamingStore streamingStore,
    ILogger<MessagePipeline> logger)
    : IMessagePipeline, IDisposable
{
    private readonly Dictionary<string, HashSet<string>> _finalizedByTopic = new();
    private readonly Dictionary<string, string> _pendingUserMessages = new();
    private readonly Subject<MessageLifecycleEvent> _lifecycleEvents = new();
    private readonly Lock _lock = new();

    public IObservable<MessageLifecycleEvent> LifecycleEvents => _lifecycleEvents;

    public string SubmitUserMessage(string topicId, string content, string? senderId)
    {
        var correlationId = Guid.NewGuid().ToString("N");

        lock (_lock)
        {
            _pendingUserMessages[correlationId] = topicId;

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Pipeline.SubmitUserMessage: topic={TopicId}, correlationId={CorrelationId}, senderId={SenderId}",
                    topicId, correlationId, senderId);
            }
        }

        dispatcher.Dispatch(new AddMessage(topicId, new ChatMessageModel
        {
            Role = "user",
            Content = content,
            SenderId = senderId,
            Timestamp = DateTimeOffset.UtcNow
        }));

        return correlationId;
    }

    public void AccumulateChunk(string topicId, string? messageId,
        string? content, string? reasoning, string? toolCalls)
    {
        lock (_lock)
        {
            if (!ShouldProcess(topicId, messageId))
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(
                        "Pipeline.AccumulateChunk: SKIPPED (already finalized) topic={TopicId}, messageId={MessageId}",
                        topicId, messageId);
                }

                return;
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Pipeline.AccumulateChunk: topic={TopicId}, messageId={MessageId}, contentLen={ContentLen}",
                    topicId, messageId, content?.Length ?? 0);
            }
        }

        // Dispatch StreamChunk - this still uses the existing reducer for now
        // Phase 3 will simplify this
        dispatcher.Dispatch(new StreamChunk(topicId, content, reasoning, toolCalls, messageId));
    }

    public void FinalizeMessage(string topicId, string? messageId)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(messageId))
            {
                if (!_finalizedByTopic.TryGetValue(topicId, out var finalized))
                {
                    finalized = new HashSet<string>();
                    _finalizedByTopic[topicId] = finalized;
                }

                if (!finalized.Add(messageId))
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug(
                            "Pipeline.FinalizeMessage: SKIPPED (already finalized) topic={TopicId}, messageId={MessageId}",
                            topicId, messageId);
                    }

                    return;
                }
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Pipeline.FinalizeMessage: topic={TopicId}, messageId={MessageId}",
                    topicId, messageId);
            }
        }

        // Get current streaming content and add as message
        var streamingContent = streamingStore.State.StreamingByTopic.GetValueOrDefault(topicId);
        if (streamingContent?.HasContent == true)
        {
            dispatcher.Dispatch(new AddMessage(
                topicId,
                new ChatMessageModel
                {
                    Role = "assistant",
                    Content = streamingContent.Content,
                    Reasoning = streamingContent.Reasoning,
                    ToolCalls = streamingContent.ToolCalls,
                    MessageId = messageId
                },
                messageId));

            dispatcher.Dispatch(new ResetStreamingContent(topicId));
        }
    }

    public void LoadHistory(string topicId, IEnumerable<ChatHistoryMessage> messages)
    {
        var chatMessages = messages.Select(h => new ChatMessageModel
        {
            Role = h.Role,
            Content = h.Content,
            MessageId = h.MessageId,
            SenderId = h.SenderId,
            Timestamp = h.Timestamp
        }).ToList();

        lock (_lock)
        {
            // Track all message IDs as finalized
            if (!_finalizedByTopic.TryGetValue(topicId, out var finalized))
            {
                finalized = new HashSet<string>();
                _finalizedByTopic[topicId] = finalized;
            }

            foreach (var msg in chatMessages.Where(m => !string.IsNullOrEmpty(m.MessageId)))
            {
                finalized.Add(msg.MessageId!);
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Pipeline.LoadHistory: topic={TopicId}, count={Count}, finalizedIds={FinalizedCount}",
                    topicId, chatMessages.Count, finalized.Count);
            }
        }

        dispatcher.Dispatch(new MessagesLoaded(topicId, chatMessages));
    }

    public void ResumeFromBuffer(string topicId, IReadOnlyList<ChatStreamMessage> buffer,
        string? currentMessageId, string? currentPrompt, string? currentSenderId)
    {
        var existingMessages = messagesStore.State.MessagesByTopic
            .GetValueOrDefault(topicId) ?? [];

        var historyById = existingMessages
            .Where(m => !string.IsNullOrEmpty(m.MessageId))
            .ToDictionary(m => m.MessageId!, m => m);

        // Don't strip completed turn content — we need anchor MessageIds for positioning.
        // Only strip the streaming message (below).
        var (completedTurns, rawStreamingMessage) =
            BufferRebuildUtility.RebuildFromBuffer(buffer, []);

        // Strip streaming message against history content
        var historyContent = existingMessages
            .Where(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.Content))
            .Select(m => m.Content)
            .ToHashSet();
        var streamingMessage = BufferRebuildUtility.StripKnownContent(rawStreamingMessage, historyContent);

        lock (_lock)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Pipeline.ResumeFromBuffer: topic={TopicId}, bufferCount={BufferCount}, " +
                    "completedTurns={CompletedTurns}, hasStreamingContent={HasStreaming}",
                    topicId, buffer.Count, completedTurns.Count, streamingMessage.HasContent);
            }
        }

        // Classify completed turns: anchors (MessageId in history) track position,
        // new messages are grouped by which anchor they follow
        string? lastAnchorId = null;
        var followingNew = new Dictionary<string, List<ChatMessageModel>>();
        var leadingNew = new List<ChatMessageModel>();

        foreach (var turn in completedTurns.Where(t =>
                     t.HasContent && !(t.Role == "user" && t.Content == currentPrompt)))
        {
            if (!string.IsNullOrEmpty(turn.MessageId) && historyById.ContainsKey(turn.MessageId))
            {
                followingNew[turn.MessageId] = [];
                lastAnchorId = turn.MessageId;
            }
            else if (lastAnchorId is not null)
            {
                followingNew[lastAnchorId].Add(turn);
            }
            else
            {
                leadingNew.Add(turn);
            }
        }

        // Build merged list: walk history, enrich anchors, insert new messages at anchor positions
        var merged = new List<ChatMessageModel>(existingMessages.Count + completedTurns.Count);
        var leadingInserted = false;

        foreach (var msg in existingMessages)
        {
            // Insert leading new messages before the first anchor
            if (!leadingInserted && msg.MessageId is not null && followingNew.ContainsKey(msg.MessageId))
            {
                merged.AddRange(leadingNew);
                leadingInserted = true;
            }

            // Enrich anchor with buffer reasoning/toolcalls, or pass through unchanged
            var anchorTurn = (msg.MessageId is not null &&
                completedTurns.FirstOrDefault(t => t.MessageId == msg.MessageId) is { } match)
                ? match : null;

            if (anchorTurn is not null)
            {
                var nr = string.IsNullOrEmpty(msg.Reasoning) && !string.IsNullOrEmpty(anchorTurn.Reasoning);
                var nt = string.IsNullOrEmpty(msg.ToolCalls) && !string.IsNullOrEmpty(anchorTurn.ToolCalls);
                merged.Add((nr || nt)
                    ? msg with
                    {
                        Reasoning = nr ? anchorTurn.Reasoning : msg.Reasoning,
                        ToolCalls = nt ? anchorTurn.ToolCalls : msg.ToolCalls
                    }
                    : msg);
            }
            else
            {
                merged.Add(msg);
            }

            // Insert new messages that follow this anchor
            if (msg.MessageId is not null && followingNew.TryGetValue(msg.MessageId, out var following))
            {
                merged.AddRange(following);
            }
        }

        // Append leading new if no anchors were found
        if (!leadingInserted)
        {
            merged.AddRange(leadingNew);
        }

        // Add current prompt if not already present
        if (!string.IsNullOrEmpty(currentPrompt) &&
            !existingMessages.Any(m => m.Role == "user" && m.Content == currentPrompt))
        {
            merged.Add(new ChatMessageModel
            {
                Role = "user",
                Content = currentPrompt,
                SenderId = currentSenderId
            });
        }

        dispatcher.Dispatch(new MessagesLoaded(topicId, merged));

        // Streaming content — enrich history or dispatch chunk
        if (streamingMessage.HasContent)
        {
            if (!string.IsNullOrEmpty(currentMessageId) &&
                historyById.TryGetValue(currentMessageId, out var historyMsg))
            {
                var needsReasoning = string.IsNullOrEmpty(historyMsg.Reasoning) && !string.IsNullOrEmpty(streamingMessage.Reasoning);
                var needsToolCalls = string.IsNullOrEmpty(historyMsg.ToolCalls) && !string.IsNullOrEmpty(streamingMessage.ToolCalls);

                if (needsReasoning || needsToolCalls)
                {
                    var enriched = historyMsg with
                    {
                        Reasoning = needsReasoning ? streamingMessage.Reasoning : historyMsg.Reasoning,
                        ToolCalls = needsToolCalls ? streamingMessage.ToolCalls : historyMsg.ToolCalls
                    };
                    dispatcher.Dispatch(new UpdateMessage(topicId, historyMsg.MessageId!, enriched));
                    return;
                }
            }

            dispatcher.Dispatch(new StreamChunk(
                topicId,
                streamingMessage.Content,
                streamingMessage.Reasoning,
                streamingMessage.ToolCalls,
                currentMessageId));
        }
    }

    public void Reset(string topicId)
    {
        lock (_lock)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Pipeline.Reset: topic={TopicId}", topicId);
            }
        }

        dispatcher.Dispatch(new ResetStreamingContent(topicId));
    }

    public bool WasSentByThisClient(string? correlationId)
    {
        if (string.IsNullOrEmpty(correlationId))
        {
            return false;
        }

        lock (_lock)
        {
            return _pendingUserMessages.ContainsKey(correlationId);
        }
    }

    public PipelineSnapshot GetSnapshot(string topicId)
    {
        lock (_lock)
        {
            var streamingContent = streamingStore.State.StreamingByTopic.GetValueOrDefault(topicId);
            var streamingId = streamingContent?.CurrentMessageId;
            var finalizedCount = _finalizedByTopic.GetValueOrDefault(topicId)?.Count ?? 0;
            var pendingCount = _pendingUserMessages.Count;

            return new PipelineSnapshot(streamingId, finalizedCount, pendingCount, []);
        }
    }

    private bool ShouldProcess(string topicId, string? messageId)
    {
        if (string.IsNullOrEmpty(messageId))
        {
            return true;
        }

        if (_finalizedByTopic.TryGetValue(topicId, out var finalized) &&
            finalized.Contains(messageId))
        {
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        _lifecycleEvents.Dispose();
    }
}