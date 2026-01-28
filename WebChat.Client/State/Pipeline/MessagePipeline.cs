using System.Reactive.Subjects;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging;
using WebChat.Client.Models;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;

namespace WebChat.Client.State.Pipeline;

public sealed class MessagePipeline : IMessagePipeline, IDisposable
{
    private readonly IDispatcher _dispatcher;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly ILogger<MessagePipeline> _logger;

    private readonly Dictionary<string, ManagedMessage> _messagesById = new();
    private readonly Dictionary<string, HashSet<string>> _finalizedByTopic = new();
    private readonly Dictionary<string, string> _pendingUserMessages = new();
    private readonly Dictionary<string, string> _streamingByTopic = new();
    private readonly Subject<MessageLifecycleEvent> _lifecycleEvents = new();
    private readonly object _lock = new();

    public IObservable<MessageLifecycleEvent> LifecycleEvents => _lifecycleEvents;

    public MessagePipeline(
        IDispatcher dispatcher,
        MessagesStore messagesStore,
        StreamingStore streamingStore,
        ILogger<MessagePipeline> logger)
    {
        _dispatcher = dispatcher;
        _messagesStore = messagesStore;
        _streamingStore = streamingStore;
        _logger = logger;
    }

    public string SubmitUserMessage(string topicId, string content, string? senderId)
    {
        var correlationId = Guid.NewGuid().ToString("N");

        lock (_lock)
        {
            _pendingUserMessages[correlationId] = topicId;

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Pipeline.SubmitUserMessage: topic={TopicId}, correlationId={CorrelationId}, senderId={SenderId}",
                    topicId, correlationId, senderId);
            }
        }

        _dispatcher.Dispatch(new AddMessage(topicId, new ChatMessageModel
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
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Pipeline.AccumulateChunk: SKIPPED (already finalized) topic={TopicId}, messageId={MessageId}",
                        topicId, messageId);
                }
                return;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Pipeline.AccumulateChunk: topic={TopicId}, messageId={MessageId}, contentLen={ContentLen}",
                    topicId, messageId, content?.Length ?? 0);
            }
        }

        // Dispatch StreamChunk - this still uses the existing reducer for now
        // Phase 3 will simplify this
        _dispatcher.Dispatch(new StreamChunk(topicId, content, reasoning, toolCalls, messageId));
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

                if (finalized.Contains(messageId))
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(
                            "Pipeline.FinalizeMessage: SKIPPED (already finalized) topic={TopicId}, messageId={MessageId}",
                            topicId, messageId);
                    }
                    return;
                }

                finalized.Add(messageId);
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Pipeline.FinalizeMessage: topic={TopicId}, messageId={MessageId}",
                    topicId, messageId);
            }
        }

        // Get current streaming content and add as message
        var streamingContent = _streamingStore.State.StreamingByTopic.GetValueOrDefault(topicId);
        if (streamingContent?.HasContent == true)
        {
            _dispatcher.Dispatch(new AddMessage(
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

            _dispatcher.Dispatch(new ResetStreamingContent(topicId));
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

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Pipeline.LoadHistory: topic={TopicId}, count={Count}, finalizedIds={FinalizedCount}",
                    topicId, chatMessages.Count, finalized.Count);
            }
        }

        _dispatcher.Dispatch(new MessagesLoaded(topicId, chatMessages));
    }

    public void ResumeFromBuffer(string topicId, IReadOnlyList<ChatStreamMessage> buffer,
        string? currentMessageId, string? currentPrompt, string? currentSenderId)
    {
        // Delegate to existing BufferRebuildUtility for now
        // This maintains compatibility while migrating
        var existingMessages = _messagesStore.State.MessagesByTopic
            .GetValueOrDefault(topicId) ?? [];

        var historyContent = existingMessages
            .Where(m => m.Role == "assistant" && !string.IsNullOrEmpty(m.Content))
            .Select(m => m.Content)
            .ToHashSet();

        var (completedTurns, streamingMessage) =
            Services.Streaming.BufferRebuildUtility.RebuildFromBuffer(buffer, historyContent);

        lock (_lock)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
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
                _dispatcher.Dispatch(new AddMessage(topicId, new ChatMessageModel
                {
                    Role = "user",
                    Content = currentPrompt,
                    SenderId = currentSenderId
                }));
            }
        }

        // Add completed turns (skip user messages matching currentPrompt)
        foreach (var turn in completedTurns.Where(t =>
            t.HasContent && !(t.Role == "user" && t.Content == currentPrompt)))
        {
            _dispatcher.Dispatch(new AddMessage(topicId, turn));
        }

        // Dispatch streaming content
        if (streamingMessage.HasContent)
        {
            _dispatcher.Dispatch(new StreamChunk(
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
            _streamingByTopic.Remove(topicId);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Pipeline.Reset: topic={TopicId}", topicId);
            }
        }

        _dispatcher.Dispatch(new ResetStreamingContent(topicId));
    }

    public bool WasSentByThisClient(string? correlationId)
    {
        if (string.IsNullOrEmpty(correlationId))
            return false;

        lock (_lock)
        {
            return _pendingUserMessages.ContainsKey(correlationId);
        }
    }

    public PipelineSnapshot GetSnapshot(string topicId)
    {
        lock (_lock)
        {
            var streamingId = _streamingByTopic.GetValueOrDefault(topicId);
            var finalizedCount = _finalizedByTopic.GetValueOrDefault(topicId)?.Count ?? 0;
            var pendingCount = _pendingUserMessages.Count;
            var activeMessages = _messagesById.Values
                .Where(m => m.TopicId == topicId)
                .ToList();

            return new PipelineSnapshot(streamingId, finalizedCount, pendingCount, activeMessages);
        }
    }

    private bool ShouldProcess(string topicId, string? messageId)
    {
        if (string.IsNullOrEmpty(messageId))
            return true;

        if (_finalizedByTopic.TryGetValue(topicId, out var finalized) &&
            finalized.Contains(messageId))
            return false;

        return true;
    }

    public void Dispose()
    {
        _lifecycleEvents.Dispose();
    }
}
