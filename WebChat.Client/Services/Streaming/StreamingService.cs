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
        var receivedNewContent = false;

        // Track processed lengths to avoid duplicate display when resuming from buffer.
        // For fresh streams these start at 0, so all content is considered new.
        var processedContentLength = streamingMessage.Content.Length;
        var processedReasoningLength = streamingMessage.Reasoning?.Length ?? 0;
        var processedToolCallsLength = streamingMessage.ToolCalls?.Length ?? 0;

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
                        dispatcher.Dispatch(new AddMessage(topic.TopicId, streamingMessage, currentMessageId));
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

                if (isNewMessageTurn && streamingMessage.HasContent)
                {
                    dispatcher.Dispatch(new AddMessage(topic.TopicId, streamingMessage, currentMessageId));
                    streamingMessage = new ChatMessageModel { Role = "assistant" };
                    dispatcher.Dispatch(new ResetStreamingContent(topic.TopicId));

                    processedContentLength = 0;
                    processedReasoningLength = 0;
                    processedToolCallsLength = 0;
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

                receivedNewContent = true;
                dispatcher.Dispatch(new StreamChunk(
                    topic.TopicId,
                    streamingMessage.Content,
                    streamingMessage.Reasoning,
                    streamingMessage.ToolCalls,
                    currentMessageId));
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
                dispatcher.Dispatch(new AddMessage(topic.TopicId, streamingMessage, currentMessageId));
            }

            if (receivedNewContent)
            {
                var currentTopic = topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topic.TopicId);
                if (currentTopic is not null)
                {
                    var isActivelyViewed = topicsStore.State.SelectedTopicId == topic.TopicId;
                    var lastMsgId = isActivelyViewed ? currentMessageId : currentTopic.LastReadMessageId;

                    var metadata = currentTopic.ToMetadata() with
                    {
                        LastMessageAt = DateTimeOffset.UtcNow,
                        LastReadMessageId = lastMsgId
                    };

                    var updatedTopic = StoredTopic.FromMetadata(metadata);
                    dispatcher.Dispatch(new UpdateTopic(updatedTopic));
                    await topicService.SaveTopicAsync(metadata);
                }
            }
        }
        catch (Exception ex) when (!TransientErrorFilter.IsTransientException(ex))
        {
            dispatcher.Dispatch(new ShowError(ex.Message));
        }
        catch
        {
            // Transient errors silently ignored - reconnection handles recovery
        }
        finally
        {
            dispatcher.Dispatch(new StreamCompleted(topic.TopicId));
        }
    }
}