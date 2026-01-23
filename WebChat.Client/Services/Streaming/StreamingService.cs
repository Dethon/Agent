using System.Collections.Concurrent;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace WebChat.Client.Services.Streaming;

public sealed class StreamingService(
    IChatMessagingService messagingService,
    IDispatcher dispatcher,
    ITopicService topicService,
    TopicsStore topicsStore) : IStreamingService
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
        var needsReasoningSeparator = false;

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
                    streamingMessage = streamingMessage with
                    {
                        Content = chunk.Error,
                        IsError = true
                    };
                    dispatcher.Dispatch(new StreamChunk(
                        topic.TopicId,
                        streamingMessage.Content,
                        streamingMessage.Reasoning,
                        streamingMessage.ToolCalls,
                        currentMessageId));
                    break;
                }

                // Skip user messages - they're handled separately via UserMessageNotification
                if (chunk.UserMessage is not null)
                {
                    continue;
                }

                var isNewMessageTurn = chunk.MessageId != currentMessageId && currentMessageId is not null;

                // Only finalize current message if new chunk starts actual message content
                // Tool calls (no content/reasoning) shouldn't split the message
                var chunkStartsNewMessage =
                    !string.IsNullOrEmpty(chunk.Content) || !string.IsNullOrEmpty(chunk.Reasoning);
                if (isNewMessageTurn && !string.IsNullOrEmpty(streamingMessage.Content) && chunkStartsNewMessage)
                {
                    dispatcher.Dispatch(new AddMessage(topic.TopicId, streamingMessage));
                    streamingMessage = new ChatMessageModel { Role = "assistant" };
                    dispatcher.Dispatch(new StreamChunk(topic.TopicId, null, null, null, chunk.MessageId));
                    needsReasoningSeparator = false;
                }
                else if (isNewMessageTurn && !string.IsNullOrEmpty(streamingMessage.Reasoning) && chunkStartsNewMessage)
                {
                    needsReasoningSeparator = true;
                }

                currentMessageId = chunk.MessageId;

                streamingMessage =
                    BufferRebuildUtility.AccumulateChunk(streamingMessage, chunk, ref needsReasoningSeparator);
                dispatcher.Dispatch(new StreamChunk(
                    topic.TopicId,
                    streamingMessage.Content,
                    streamingMessage.Reasoning,
                    streamingMessage.ToolCalls,
                    currentMessageId));
            }

            if (streamingMessage.HasContent)
            {
                dispatcher.Dispatch(new AddMessage(topic.TopicId, streamingMessage with { }));
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
        catch (Exception ex)
        {
            dispatcher.Dispatch(new AddMessage(topic.TopicId, CreateErrorMessage($"Error: {ex.Message}")));
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
        var needsReasoningSeparator = false;
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
                    streamingMessage = streamingMessage with
                    {
                        Content = chunk.Error,
                        IsError = true
                    };
                    dispatcher.Dispatch(new StreamChunk(
                        topic.TopicId,
                        streamingMessage.Content,
                        streamingMessage.Reasoning,
                        streamingMessage.ToolCalls,
                        currentMessageId));
                    break;
                }

                // Skip user messages - they're handled separately via buffer rebuild
                if (chunk.UserMessage is not null)
                {
                    continue;
                }

                var isNewMessageTurn = chunk.MessageId != currentMessageId && currentMessageId is not null;

                // Only finalize current message if new chunk starts actual message content
                // Tool calls (no content/reasoning) shouldn't split the message
                var chunkStartsNewMessage =
                    !string.IsNullOrEmpty(chunk.Content) || !string.IsNullOrEmpty(chunk.Reasoning);
                if (isNewMessageTurn && !string.IsNullOrEmpty(streamingMessage.Content) && chunkStartsNewMessage)
                {
                    dispatcher.Dispatch(new AddMessage(topic.TopicId, streamingMessage));
                    streamingMessage = new ChatMessageModel { Role = "assistant" };
                    dispatcher.Dispatch(new StreamChunk(topic.TopicId, null, null, null, chunk.MessageId));
                    needsReasoningSeparator = false;

                    // Reset processed lengths for new message turn
                    processedContentLength = 0;
                    processedReasoningLength = 0;
                    processedToolCallsLength = 0;
                }
                else if (isNewMessageTurn && !string.IsNullOrEmpty(streamingMessage.Reasoning) && chunkStartsNewMessage)
                {
                    needsReasoningSeparator = true;
                }

                currentMessageId = chunk.MessageId;

                // Accumulate new chunks from the live stream
                // Use simple accumulation - live stream chunks are new content
                streamingMessage =
                    BufferRebuildUtility.AccumulateChunk(streamingMessage, chunk, ref needsReasoningSeparator);

                // Check if we have new content beyond what was in the buffer
                var hasNewContent = streamingMessage.Content.Length > processedContentLength;
                var hasNewReasoning = (streamingMessage.Reasoning?.Length ?? 0) > processedReasoningLength;
                var hasNewToolCalls = (streamingMessage.ToolCalls?.Length ?? 0) > processedToolCallsLength;
                var isNew = hasNewContent || hasNewReasoning || hasNewToolCalls;

                // Update processed lengths
                processedContentLength = streamingMessage.Content.Length;
                processedReasoningLength = streamingMessage.Reasoning?.Length ?? 0;
                processedToolCallsLength = streamingMessage.ToolCalls?.Length ?? 0;

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
                    currentMessageId));
            }

            if (streamingMessage.HasContent)
            {
                dispatcher.Dispatch(new AddMessage(topic.TopicId, streamingMessage with { }));
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
        catch (Exception ex)
        {
            dispatcher.Dispatch(new AddMessage(topic.TopicId,
                CreateErrorMessage($"Error resuming stream: {ex.Message}")));
        }
        finally
        {
            dispatcher.Dispatch(new StreamCompleted(topic.TopicId));
        }
    }

    private static ChatMessageModel CreateErrorMessage(string errorMessage)
    {
        return new ChatMessageModel
        {
            Role = "assistant",
            Content = errorMessage,
            IsError = true
        };
    }
}