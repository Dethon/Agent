using System.Collections.Concurrent;
using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;

namespace WebChat.Client.Services.Streaming;

public sealed class StreamingService(
    IChatMessagingService messagingService,
    IDispatcher dispatcher,
    ITopicService topicService,
    TopicsStore topicsStore,
    StreamingStore streamingStore) : IStreamingService
{
    private readonly ConcurrentDictionary<string, Task> _activeStreams = new();
    private readonly SemaphoreSlim _streamLock = new(1, 1);

    public async Task SendMessageAsync(StoredTopic topic, string message, string? correlationId = null)
    {
        await _streamLock.WaitAsync();
        try
        {
            var isNewStream = !_activeStreams.TryGetValue(topic.TopicId, out var task)
                              || task.IsCompleted;

            if (isNewStream)
            {
                StartNewStream(topic, message, correlationId);
            }
            else
            {
                var success = await messagingService.EnqueueMessageAsync(topic.TopicId, message, correlationId);
                if (!success)
                {
                    StartNewStream(topic, message, correlationId);
                }
            }
        }
        finally
        {
            _streamLock.Release();
        }
    }

    public async Task<bool> TryStartResumeStreamAsync(
        StoredTopic topic,
        ChatMessageModel streamingMessage,
        string startMessageId)
    {
        await _streamLock.WaitAsync();
        try
        {
            var hasActiveStream = _activeStreams.TryGetValue(topic.TopicId, out var task) && !task.IsCompleted;
            if (hasActiveStream)
            {
                return false;
            }

            dispatcher.Dispatch(new StreamStarted(topic.TopicId));
            var streamTask = ResumeStreamResponseAsync(topic, streamingMessage, startMessageId);
            _activeStreams[topic.TopicId] = streamTask;
            _ = streamTask.ContinueWith(_ => _activeStreams.TryRemove(topic.TopicId, out var _));
            return true;
        }
        finally
        {
            _streamLock.Release();
        }
    }

    public async Task<bool> IsStreamActiveAsync(string topicId)
    {
        await _streamLock.WaitAsync();
        try
        {
            return _activeStreams.TryGetValue(topicId, out var task) && !task.IsCompleted;
        }
        finally
        {
            _streamLock.Release();
        }
    }

    private void StartNewStream(StoredTopic topic, string message, string? correlationId)
    {
        dispatcher.Dispatch(new StreamStarted(topic.TopicId));
        var streamTask = StreamResponseAsync(topic, message, correlationId);
        _activeStreams[topic.TopicId] = streamTask;
        _ = streamTask.ContinueWith(_ => _activeStreams.TryRemove(topic.TopicId, out var _));
    }

    public Task StreamResponseAsync(StoredTopic topic, string message, string? correlationId = null)
    {
        var chunks = messagingService.SendMessageAsync(topic.TopicId, message, correlationId);
        var streamingMessage = new ChatMessageModel { Role = "assistant" };
        return ProcessStreamAsync(topic, chunks, streamingMessage, currentMessageId: null);
    }

    public Task ResumeStreamResponseAsync(
        StoredTopic topic,
        ChatMessageModel streamingMessage,
        string startMessageId)
    {
        var chunks = messagingService.ResumeStreamAsync(topic.TopicId);
        return ProcessStreamAsync(topic, chunks, streamingMessage, startMessageId);
    }

    private async Task ProcessStreamAsync(
        StoredTopic topic,
        IAsyncEnumerable<ChatStreamMessage> chunks,
        ChatMessageModel streamingMessage,
        string? currentMessageId)
    {
        // Track processed lengths to avoid duplicate display when resuming from buffer.
        // For fresh streams these start at 0, so all content is considered new.
        var processedContentLength = streamingMessage.Content.Length;
        var processedReasoningLength = streamingMessage.Reasoning?.Length ?? 0;
        var processedToolCallsLength = streamingMessage.ToolCalls?.Length ?? 0;

        // The agent's stream can interleave chunks from different assistant messages: a later
        // message's tool-call display races ahead of an earlier message's content (which lags
        // via send_reply), so MessageIds bounce instead of arriving contiguously. We keep one
        // accumulator per MessageId so a revisit can continue appending, and we route late
        // chunks for an already-committed MessageId through UpdateMessage (merging the bubble
        // in place) instead of a fresh AddMessage that AddMessageWithDedup would drop.
        var stash = new Dictionary<string, MessageAccumulator>();
        var committed = new HashSet<string>();

        try
        {
            await foreach (var chunk in chunks)
            {
                if (chunk.ApprovalRequest is not null)
                {
                    dispatcher.Dispatch(new ShowApproval(topic.TopicId, chunk.ApprovalRequest));
                    continue;
                }

                if (chunk.Error is not null)
                {
                    if (!TransientErrorFilter.IsTransientErrorMessage(chunk.Error))
                    {
                        dispatcher.Dispatch(new ShowError(chunk.Error));
                        dispatcher.Dispatch(new AddMessage(topic.TopicId, CreateErrorMessage(chunk.Error)));
                    }

                    continue;
                }

                // When a user message arrives in the stream, finalize current assistant content
                // UNLESS SendMessageEffect already finalized (check FinalizationRequests flag)
                if (chunk.UserMessage is not null)
                {
                    if (streamingStore.State.FinalizationRequests.Contains(topic.TopicId))
                    {
                        // SendMessageEffect already added the message, just clear the flag
                        dispatcher.Dispatch(new ClearFinalizationRequest(topic.TopicId));
                    }
                    else if (streamingMessage.HasContent)
                    {
                        // No finalization request - we need to add the message here
                        flush(streamingMessage, currentMessageId);
                        dispatcher.Dispatch(new ResetStreamingContent(topic.TopicId));
                    }

                    // Reset accumulator for the new response (user message added by HandleUserMessage)
                    streamingMessage = new ChatMessageModel { Role = "assistant" };
                    processedContentLength = 0;
                    processedReasoningLength = 0;
                    processedToolCallsLength = 0;

                    continue;
                }

                var isNewMessageTurn = chunk.MessageId != currentMessageId && currentMessageId is not null;

                if (isNewMessageTurn)
                {
                    if (streamingMessage.HasContent)
                    {
                        // Commit the message we're switching away from (AddMessage first time,
                        // UpdateMessage on a revisit) and stash its accumulator so a future
                        // revisit can continue appending into it.
                        flush(streamingMessage, currentMessageId);
                        if (currentMessageId is not null)
                        {
                            stash[currentMessageId] = new MessageAccumulator(
                                streamingMessage,
                                processedContentLength,
                                processedReasoningLength,
                                processedToolCallsLength);
                        }

                        dispatcher.Dispatch(new ResetStreamingContent(topic.TopicId));
                    }

                    // Restore the incoming MessageId's prior accumulator if we've seen it
                    // before (interleaving), otherwise start fresh.
                    if (chunk.MessageId is not null && stash.TryGetValue(chunk.MessageId, out var saved))
                    {
                        streamingMessage = saved.Message;
                        processedContentLength = saved.ContentLength;
                        processedReasoningLength = saved.ReasoningLength;
                        processedToolCallsLength = saved.ToolCallsLength;
                    }
                    else
                    {
                        streamingMessage = new ChatMessageModel { Role = "assistant" };
                        processedContentLength = 0;
                        processedReasoningLength = 0;
                        processedToolCallsLength = 0;
                    }
                }

                currentMessageId = chunk.MessageId;

                streamingMessage = BufferRebuildUtility.AccumulateChunk(streamingMessage, chunk);

                // Skip dispatching if no content changed beyond what we already processed
                var hasNewContent = streamingMessage.Content.Length > processedContentLength;
                var hasNewReasoning = (streamingMessage.Reasoning?.Length ?? 0) > processedReasoningLength;
                var hasNewToolCalls = (streamingMessage.ToolCalls?.Length ?? 0) > processedToolCallsLength;

                processedContentLength = streamingMessage.Content.Length;
                processedReasoningLength = streamingMessage.Reasoning?.Length ?? 0;
                processedToolCallsLength = streamingMessage.ToolCalls?.Length ?? 0;

                if (!hasNewContent && !hasNewReasoning && !hasNewToolCalls)
                {
                    continue;
                }

                // For an already-committed MessageId revisited mid-stream, update its bubble in
                // place; the live streaming buffer is only used for the current uncommitted
                // accumulator, preserving the single-live-bubble look in the contiguous case.
                if (currentMessageId is not null && committed.Contains(currentMessageId))
                {
                    dispatcher.Dispatch(new UpdateMessage(topic.TopicId, currentMessageId, streamingMessage));
                }
                else
                {
                    dispatcher.Dispatch(new StreamChunk(
                        topic.TopicId,
                        streamingMessage.Content,
                        streamingMessage.Reasoning,
                        streamingMessage.ToolCalls,
                        currentMessageId));
                }

                await UpdateLastReadMessage(topic, chunk);
            }

            // Check finalization one more time before adding final message
            // This handles the case where the stream ends right after a user message
            // (no content chunks arrive after the finalization request was dispatched)
            if (streamingStore.State.FinalizationRequests.Contains(topic.TopicId))
            {
                streamingMessage = new ChatMessageModel { Role = "assistant" };
                dispatcher.Dispatch(new ClearFinalizationRequest(topic.TopicId));
            }

            if (streamingMessage.HasContent)
            {
                flush(streamingMessage, currentMessageId);
            }
        }
        catch (Exception ex) when (!TransientErrorFilter.IsTransientException(ex))
        {
            dispatcher.Dispatch(new ShowError(ex.Message));
            dispatcher.Dispatch(new AddMessage(topic.TopicId, CreateErrorMessage(ex.Message)));
        }
        catch
        {
            // Transient errors silently ignored - reconnection handles recovery
        }
        finally
        {
            dispatcher.Dispatch(new StreamCompleted(topic.TopicId));
        }

        return;

        void flush(ChatMessageModel message, string? mid)
        {
            if (!message.HasContent)
            {
                return;
            }

            if (mid is not null && committed.Contains(mid))
            {
                dispatcher.Dispatch(new UpdateMessage(topic.TopicId, mid, message));
            }
            else
            {
                dispatcher.Dispatch(new AddMessage(topic.TopicId, message, mid));
                if (mid is not null)
                {
                    committed.Add(mid);
                }
            }
        }
    }

    private readonly record struct MessageAccumulator(
        ChatMessageModel Message,
        int ContentLength,
        int ReasoningLength,
        int ToolCallsLength);

    private static ChatMessageModel CreateErrorMessage(string content) => new()
    {
        Role = "assistant",
        Content = content,
        IsError = true,
        Timestamp = DateTimeOffset.UtcNow
    };

    private async Task UpdateLastReadMessage(StoredTopic topic, ChatStreamMessage chunk)
    {
        var currentTopic = topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topic.TopicId);
        if (currentTopic is null || chunk.MessageId is null)
        {
            return;
        }

        var isActivelyViewed = topicsStore.State.SelectedTopicId == topic.TopicId;
        var lastReadMsgId = isActivelyViewed ? chunk.MessageId : currentTopic.LastReadMessageId;

        if (lastReadMsgId is not null && lastReadMsgId == currentTopic.LastReadMessageId)
        {
            return;
        }

        var metadata = currentTopic.ToMetadata() with
        {
            LastMessageAt = DateTimeOffset.UtcNow,
            LastReadMessageId = lastReadMsgId
        };

        var updatedTopic = StoredTopic.FromMetadata(metadata);
        dispatcher.Dispatch(new UpdateTopic(updatedTopic));
        await topicService.SaveTopicAsync(metadata);
    }
}