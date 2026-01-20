using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;

namespace WebChat.Client.Services.Streaming;

public sealed class StreamingCoordinator(
    IChatMessagingService messagingService,
    IDispatcher dispatcher,
    ITopicService topicService) : IStreamingCoordinator
{
    private DateTime _lastRenderTime = DateTime.MinValue;
    private const int RenderThrottleMs = 50;
    private readonly Lock _throttleLock = new();
    private bool _renderPending;

    public async Task StreamResponseAsync(StoredTopic topic, string message, Func<Task> onRender)
    {
        var streamingMessage = new ChatMessageModel { Role = "assistant" };
        string? currentMessageId = null;
        var needsReasoningSeparator = false;

        try
        {
            await foreach (var chunk in messagingService.SendMessageAsync(topic.TopicId, message))
            {
                if (chunk.ApprovalRequest is not null)
                {
                    dispatcher.Dispatch(new ShowApproval(topic.TopicId, chunk.ApprovalRequest));
                    await onRender();
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

                var isNewMessageTurn = chunk.MessageId != currentMessageId && currentMessageId is not null;

                if (isNewMessageTurn && !string.IsNullOrEmpty(streamingMessage.Content))
                {
                    dispatcher.Dispatch(new AddMessage(topic.TopicId, streamingMessage));
                    streamingMessage = new ChatMessageModel { Role = "assistant" };
                    dispatcher.Dispatch(new StreamChunk(topic.TopicId, null, null, null, chunk.MessageId));
                    needsReasoningSeparator = false;
                }
                else if (isNewMessageTurn && !string.IsNullOrEmpty(streamingMessage.Reasoning))
                {
                    needsReasoningSeparator = true;
                }

                currentMessageId = chunk.MessageId;

                streamingMessage = AccumulateChunk(streamingMessage, chunk, ref needsReasoningSeparator);
                dispatcher.Dispatch(new StreamChunk(
                    topic.TopicId,
                    streamingMessage.Content,
                    streamingMessage.Reasoning,
                    streamingMessage.ToolCalls,
                    currentMessageId));

                await ThrottledRenderAsync(onRender);
            }

            if (streamingMessage.HasContent)
            {
                dispatcher.Dispatch(new AddMessage(topic.TopicId, streamingMessage with { }));
            }

            topic.LastMessageAt = DateTime.UtcNow;
            await topicService.SaveTopicAsync(topic.ToMetadata());
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new AddMessage(topic.TopicId, CreateErrorMessage($"Error: {ex.Message}")));
        }
        finally
        {
            dispatcher.Dispatch(new StreamCompleted(topic.TopicId));
            await onRender();
        }
    }

    public async Task ResumeStreamResponseAsync(
        StoredTopic topic,
        ChatMessageModel streamingMessage,
        string startMessageId,
        Func<Task> onRender)
    {
        var currentMessageId = startMessageId;
        var needsReasoningSeparator = false;
        var receivedNewContent = false;

        var knownContent = streamingMessage.Content;
        var knownReasoning = streamingMessage.Reasoning ?? "";
        var knownToolCalls = streamingMessage.ToolCalls ?? "";

        try
        {
            await foreach (var chunk in messagingService.ResumeStreamAsync(topic.TopicId))
            {
                if (chunk.ApprovalRequest is not null)
                {
                    dispatcher.Dispatch(new ShowApproval(topic.TopicId, chunk.ApprovalRequest));
                    await onRender();
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

                var isNewMessageTurn = chunk.MessageId != currentMessageId && currentMessageId is not null;

                if (isNewMessageTurn && !string.IsNullOrEmpty(streamingMessage.Content))
                {
                    dispatcher.Dispatch(new AddMessage(topic.TopicId, streamingMessage));
                    streamingMessage = new ChatMessageModel { Role = "assistant" };
                    dispatcher.Dispatch(new StreamChunk(topic.TopicId, null, null, null, chunk.MessageId));
                    needsReasoningSeparator = false;

                    knownContent = "";
                    knownReasoning = "";
                    knownToolCalls = "";
                }
                else if (isNewMessageTurn && !string.IsNullOrEmpty(streamingMessage.Reasoning))
                {
                    needsReasoningSeparator = true;
                }

                currentMessageId = chunk.MessageId;

                var isNew = false;

                if (!string.IsNullOrEmpty(chunk.Content) && !knownContent.Contains(chunk.Content))
                {
                    streamingMessage = streamingMessage with
                    {
                        Content = string.IsNullOrEmpty(streamingMessage.Content)
                            ? chunk.Content
                            : streamingMessage.Content + chunk.Content
                    };
                    knownContent = streamingMessage.Content;
                    isNew = true;
                }

                if (!string.IsNullOrEmpty(chunk.Reasoning) && !knownReasoning.Contains(chunk.Reasoning))
                {
                    var separator = needsReasoningSeparator ? "\n-----\n" : "";
                    needsReasoningSeparator = false;
                    streamingMessage = streamingMessage with
                    {
                        Reasoning = string.IsNullOrEmpty(streamingMessage.Reasoning)
                            ? chunk.Reasoning
                            : streamingMessage.Reasoning + separator + chunk.Reasoning
                    };
                    knownReasoning = streamingMessage.Reasoning ?? "";
                    isNew = true;
                }

                if (!string.IsNullOrEmpty(chunk.ToolCalls) && !knownToolCalls.Contains(chunk.ToolCalls))
                {
                    streamingMessage = streamingMessage with
                    {
                        ToolCalls = string.IsNullOrEmpty(streamingMessage.ToolCalls)
                            ? chunk.ToolCalls
                            : streamingMessage.ToolCalls + "\n" + chunk.ToolCalls
                    };
                    knownToolCalls = streamingMessage.ToolCalls ?? "";
                    isNew = true;
                }

                if (isNew)
                {
                    receivedNewContent = true;
                    dispatcher.Dispatch(new StreamChunk(
                        topic.TopicId,
                        streamingMessage.Content,
                        streamingMessage.Reasoning,
                        streamingMessage.ToolCalls,
                        currentMessageId));
                    await ThrottledRenderAsync(onRender);
                }
            }

            if (streamingMessage.HasContent)
            {
                dispatcher.Dispatch(new AddMessage(topic.TopicId, streamingMessage with { }));
            }

            if (receivedNewContent)
            {
                topic.LastMessageAt = DateTime.UtcNow;
                await topicService.SaveTopicAsync(topic.ToMetadata());
            }
        }
        catch (Exception ex)
        {
            dispatcher.Dispatch(new AddMessage(topic.TopicId, CreateErrorMessage($"Error resuming stream: {ex.Message}")));
        }
        finally
        {
            dispatcher.Dispatch(new StreamCompleted(topic.TopicId));
            await onRender();
        }
    }

    public (List<ChatMessageModel> CompletedTurns, ChatMessageModel StreamingMessage) RebuildFromBuffer(
        IReadOnlyList<ChatStreamMessage> bufferedMessages,
        HashSet<string> historyContent)
    {
        var currentMessage = new ChatMessageModel { Role = "assistant" };
        var completedTurns = new List<ChatMessageModel>();

        if (bufferedMessages.Count == 0)
        {
            return (completedTurns, currentMessage);
        }

        var turnGroups = bufferedMessages
            .GroupBy(m => m.MessageId)
            .OrderBy(g => g.Key)
            .ToList();

        var isFirstGroup = true;
        var needsReasoningSeparator = false;

        foreach (var turnGroup in turnGroups)
        {
            var chunks = turnGroup.ToList();
            var isComplete = chunks.Any(m => m.IsComplete);

            if (!isFirstGroup && !string.IsNullOrEmpty(currentMessage.Content))
            {
                var strippedMessage = StripKnownContent(currentMessage, historyContent);
                if (strippedMessage.HasContent)
                {
                    completedTurns.Add(strippedMessage);
                }

                currentMessage = new ChatMessageModel { Role = "assistant" };
                needsReasoningSeparator = false;
            }
            else if (!isFirstGroup && !string.IsNullOrEmpty(currentMessage.Reasoning))
            {
                needsReasoningSeparator = true;
            }

            isFirstGroup = false;

            foreach (var chunk in chunks.Where(m => m is { IsComplete: false, Error: null }))
            {
                currentMessage = AccumulateChunk(currentMessage, chunk, ref needsReasoningSeparator);
            }

            if (isComplete)
            {
                var strippedMessage = StripKnownContent(currentMessage, historyContent);
                if (strippedMessage.HasContent)
                {
                    completedTurns.Add(strippedMessage);
                }

                currentMessage = new ChatMessageModel { Role = "assistant" };
            }
        }

        var streamingMessage = StripKnownContent(currentMessage, historyContent);
        return (completedTurns, streamingMessage);
    }

    public ChatMessageModel StripKnownContent(ChatMessageModel message, HashSet<string> historyContent)
    {
        if (string.IsNullOrEmpty(message.Content))
        {
            return message;
        }

        // If the buffer content is a subset of any history content, it's a duplicate
        // (user disconnected mid-stream and buffer has incomplete content while history has complete)
        if (historyContent.Any(known => known.Contains(message.Content)))
        {
            return message with { Content = "" };
        }

        // Remove any history content that appears as a prefix in this message
        // (buffer has more content than was in history)
        var content = message.Content;
        foreach (var known in historyContent.Where(known => content.StartsWith(known)))
        {
            content = content[known.Length..].TrimStart();
        }

        return content != message.Content ? message with { Content = content } : message;
    }

    private static ChatMessageModel AccumulateChunk(
        ChatMessageModel streamingMessage,
        ChatStreamMessage chunk,
        ref bool needsReasoningSeparator)
    {
        if (!string.IsNullOrEmpty(chunk.Content))
        {
            streamingMessage = streamingMessage with
            {
                Content = string.IsNullOrEmpty(streamingMessage.Content)
                    ? chunk.Content
                    : streamingMessage.Content + chunk.Content
            };
        }

        if (!string.IsNullOrEmpty(chunk.Reasoning))
        {
            var separator = needsReasoningSeparator ? "\n-----\n" : "";
            needsReasoningSeparator = false;
            streamingMessage = streamingMessage with
            {
                Reasoning = string.IsNullOrEmpty(streamingMessage.Reasoning)
                    ? chunk.Reasoning
                    : streamingMessage.Reasoning + separator + chunk.Reasoning
            };
        }

        if (!string.IsNullOrEmpty(chunk.ToolCalls))
        {
            streamingMessage = streamingMessage with
            {
                ToolCalls = string.IsNullOrEmpty(streamingMessage.ToolCalls)
                    ? chunk.ToolCalls
                    : streamingMessage.ToolCalls + "\n" + chunk.ToolCalls
            };
        }

        return streamingMessage;
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

    private async Task ThrottledRenderAsync(Func<Task> renderCallback)
    {
        bool shouldRender;
        var delay = 0;

        lock (_throttleLock)
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastRenderTime).TotalMilliseconds;

            if (elapsed >= RenderThrottleMs)
            {
                _lastRenderTime = now;
                _renderPending = false;
                shouldRender = true;
            }
            else if (!_renderPending)
            {
                _renderPending = true;
                delay = RenderThrottleMs - (int)elapsed;
                shouldRender = false;
            }
            else
            {
                shouldRender = false;
            }
        }

        if (shouldRender)
        {
            await renderCallback();
        }
        else if (delay > 0)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(delay);
                bool shouldDelayedRender;

                lock (_throttleLock)
                {
                    shouldDelayedRender = _renderPending;
                    if (shouldDelayedRender)
                    {
                        _renderPending = false;
                        _lastRenderTime = DateTime.UtcNow;
                    }
                }

                if (shouldDelayedRender)
                {
                    try
                    {
                        await renderCallback();
                    }
                    catch
                    {
                        // Swallow render errors in delayed callback to prevent crashes
                    }
                }
            });
        }
    }
}
