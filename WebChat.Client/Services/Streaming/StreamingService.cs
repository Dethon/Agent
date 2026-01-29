using System.Collections.Concurrent;
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
        _ = streamTask.ContinueWith(_ => _activeStreams.TryRemove(topic.TopicId, out Task? _));
    }

    public async Task StreamResponseAsync(StoredTopic topic, string message, string? correlationId = null)
    {
        var streamingMessage = new ChatMessageModel { Role = "assistant" };
        string? currentMessageId = null;

        try
        {
            await foreach (var chunk in messagingService.SendMessageAsync(topic.TopicId, message, correlationId))
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
                    continue;
                }

                var isNewMessageTurn = chunk.MessageId != currentMessageId && currentMessageId is not null;

                if (isNewMessageTurn && streamingMessage.HasContent)
                {
                    dispatcher.Dispatch(new AddMessage(topic.TopicId, streamingMessage, currentMessageId));
                    streamingMessage = new ChatMessageModel { Role = "assistant" };
                    dispatcher.Dispatch(new StreamChunk(topic.TopicId, null, null, null, chunk.MessageId));
                }

                currentMessageId = chunk.MessageId;

                streamingMessage = BufferRebuildUtility.AccumulateChunk(streamingMessage, chunk);
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

            // Fetch current topic from store to get latest LastReadMessageCount
            // Don't mutate the store object - create metadata with updated LastMessageAt
            var currentTopic = topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topic.TopicId);
            if (currentTopic is not null)
            {
                var metadata = currentTopic.ToMetadata() with { LastMessageAt = DateTimeOffset.UtcNow };
                await topicService.SaveTopicAsync(metadata);
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

    public async Task ResumeStreamResponseAsync(
        StoredTopic topic,
        ChatMessageModel streamingMessage,
        string startMessageId)
    {
        var currentMessageId = startMessageId;
        var receivedNewContent = false;

        // Track the exact length of content we've already processed from the buffer
        // to avoid duplicate display. Live stream chunks are appended beyond this point.
        var processedContentLength = streamingMessage.Content.Length;
        var processedReasoningLength = streamingMessage.Reasoning?.Length ?? 0;
        var processedToolCallsLength = streamingMessage.ToolCalls?.Length ?? 0;

        try
        {
            await foreach (var chunk in messagingService.ResumeStreamAsync(topic.TopicId))
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
                    dispatcher.Dispatch(new StreamChunk(topic.TopicId, null, null, null, chunk.MessageId));

                    processedContentLength = 0;
                    processedReasoningLength = 0;
                    processedToolCallsLength = 0;
                }

                currentMessageId = chunk.MessageId;

                streamingMessage = BufferRebuildUtility.AccumulateChunk(streamingMessage, chunk);

                var newContent = streamingMessage.Content;
                var newReasoning = streamingMessage.Reasoning;
                var newToolCalls = streamingMessage.ToolCalls;

                // Check if we have new content beyond what was in the buffer
                var hasNewContent = newContent.Length > processedContentLength;
                var hasNewReasoning = (newReasoning?.Length ?? 0) > processedReasoningLength;
                var hasNewToolCalls = (newToolCalls?.Length ?? 0) > processedToolCallsLength;
                var isNew = hasNewContent || hasNewReasoning || hasNewToolCalls || chunk.MessageId != currentMessageId;

                // Update processed lengths
                processedContentLength = newContent.Length;
                processedReasoningLength = newReasoning?.Length ?? 0;
                processedToolCallsLength = newToolCalls?.Length ?? 0;

                if (!isNew)
                {
                    continue;
                }

                receivedNewContent = true;
                dispatcher.Dispatch(new StreamChunk(
                    topic.TopicId,
                    streamingMessage.Content,
                    streamingMessage.Reasoning,
                    streamingMessage.ToolCalls,
                    currentMessageId ?? chunk.MessageId));
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
                // Fetch current topic from store to get latest LastReadMessageCount
                // Don't mutate the store object - create metadata with updated LastMessageAt
                var currentTopic = topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topic.TopicId);
                if (currentTopic is not null)
                {
                    var metadata = currentTopic.ToMetadata() with { LastMessageAt = DateTimeOffset.UtcNow };
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