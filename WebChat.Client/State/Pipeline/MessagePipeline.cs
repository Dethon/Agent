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

        // Build maps for merging: history content for dedup, and history messages by ID for merging
        var historyContent = existingMessages
            .Where(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.Content))
            .Select(m => m.Content)
            .ToHashSet();

        var historyById = existingMessages
            .Where(m => !string.IsNullOrEmpty(m.MessageId))
            .ToDictionary(m => m.MessageId!, m => m);

        var (completedTurns, streamingMessage) =
            BufferRebuildUtility.RebuildFromBuffer(buffer, historyContent);

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

        // Add current prompt if not already present
        if (!string.IsNullOrEmpty(currentPrompt))
        {
            var promptExists = existingMessages.Any(m =>
                m.Role == "user" && m.Content == currentPrompt);

            if (!promptExists)
            {
                dispatcher.Dispatch(new AddMessage(topicId, new ChatMessageModel
                {
                    Role = "user",
                    Content = currentPrompt,
                    SenderId = currentSenderId
                }));
            }
        }

        // Process completed turns - merge with history or add new
        foreach (var turn in completedTurns.Where(t =>
                     t.HasContent && !(t.Role == "user" && t.Content == currentPrompt)))
        {
            // Try to merge reasoning/toolcalls into existing history message
            if (!string.IsNullOrEmpty(turn.MessageId) &&
                historyById.TryGetValue(turn.MessageId, out var historyMsg) &&
                TryMergeIntoHistory(topicId, historyMsg, turn))
            {
                continue;
            }

            dispatcher.Dispatch(new AddMessage(topicId, turn, turn.MessageId));
        }

        // For streaming content, also try to merge into history first
        if (streamingMessage.HasContent)
        {
            if (!string.IsNullOrEmpty(currentMessageId) &&
                historyById.TryGetValue(currentMessageId, out var historyMsg) &&
                TryMergeIntoHistory(topicId, historyMsg, streamingMessage))
            {
                return;
            }

            dispatcher.Dispatch(new StreamChunk(
                topicId,
                streamingMessage.Content,
                streamingMessage.Reasoning,
                streamingMessage.ToolCalls,
                currentMessageId));
        }
    }

    private bool TryMergeIntoHistory(string topicId, ChatMessageModel historyMsg, ChatMessageModel bufferMsg)
    {
        // Check if history is missing reasoning or toolcalls that buffer has
        var needsReasoning = string.IsNullOrEmpty(historyMsg.Reasoning) && !string.IsNullOrEmpty(bufferMsg.Reasoning);
        var needsToolCalls = string.IsNullOrEmpty(historyMsg.ToolCalls) && !string.IsNullOrEmpty(bufferMsg.ToolCalls);

        if (!needsReasoning && !needsToolCalls)
        {
            return false;
        }

        var merged = historyMsg with
        {
            Reasoning = needsReasoning ? bufferMsg.Reasoning : historyMsg.Reasoning,
            ToolCalls = needsToolCalls ? bufferMsg.ToolCalls : historyMsg.ToolCalls
        };

        dispatcher.Dispatch(new UpdateMessage(topicId, historyMsg.MessageId!, merged));
        return true;
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