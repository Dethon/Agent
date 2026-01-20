using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;

namespace WebChat.Client.Services.Streaming;

/// <summary>
/// Service for streaming AI responses. Uses store actions for all state updates.
/// Throttling is handled by RenderCoordinator in components, not here.
/// </summary>
public sealed class StreamingService(
    IChatMessagingService messagingService,
    IDispatcher dispatcher,
    ITopicService topicService) : IStreamingService
{
    public async Task StreamResponseAsync(StoredTopic topic, string message)
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

                // Only finalize current message if new chunk has content (starting a new response)
                // Tool calls arriving (no content) shouldn't split the message
                if (isNewMessageTurn && !string.IsNullOrEmpty(streamingMessage.Content) && !string.IsNullOrEmpty(chunk.Content))
                {
                    dispatcher.Dispatch(new AddMessage(topic.TopicId, streamingMessage));
                    streamingMessage = new ChatMessageModel { Role = "assistant" };
                    dispatcher.Dispatch(new StreamChunk(topic.TopicId, null, null, null, chunk.MessageId));
                    needsReasoningSeparator = false;
                }
                else if (isNewMessageTurn && !string.IsNullOrEmpty(streamingMessage.Reasoning) && !string.IsNullOrEmpty(chunk.Content))
                {
                    needsReasoningSeparator = true;
                }

                currentMessageId = chunk.MessageId;

                streamingMessage = BufferRebuildUtility.AccumulateChunk(streamingMessage, chunk, ref needsReasoningSeparator);
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

                // Only finalize current message if new chunk has content (starting a new response)
                // Tool calls arriving (no content) shouldn't split the message
                if (isNewMessageTurn && !string.IsNullOrEmpty(streamingMessage.Content) && !string.IsNullOrEmpty(chunk.Content))
                {
                    dispatcher.Dispatch(new AddMessage(topic.TopicId, streamingMessage));
                    streamingMessage = new ChatMessageModel { Role = "assistant" };
                    dispatcher.Dispatch(new StreamChunk(topic.TopicId, null, null, null, chunk.MessageId));
                    needsReasoningSeparator = false;

                    knownContent = "";
                    knownReasoning = "";
                    knownToolCalls = "";
                }
                else if (isNewMessageTurn && !string.IsNullOrEmpty(streamingMessage.Reasoning) && !string.IsNullOrEmpty(chunk.Content))
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
