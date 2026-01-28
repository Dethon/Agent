using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.Services;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Hub;

public sealed class HubEventDispatcher(
    IDispatcher dispatcher,
    TopicsStore topicsStore,
    StreamingStore streamingStore,
    IMessagePipeline pipeline,
    IStreamResumeService streamResumeService) : IHubEventDispatcher
{
    public void HandleTopicChanged(TopicChangedNotification notification)
    {
        switch (notification.ChangeType)
        {
            case TopicChangeType.Created when notification.Topic is not null:
                dispatcher.Dispatch(new AddTopic(StoredTopic.FromMetadata(notification.Topic)));
                break;
            case TopicChangeType.Updated when notification.Topic is not null:
                dispatcher.Dispatch(new UpdateTopic(StoredTopic.FromMetadata(notification.Topic)));
                break;
            case TopicChangeType.Deleted:
                dispatcher.Dispatch(new RemoveTopic(notification.TopicId));
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(notification),
                    notification.ChangeType,
                    "Invalid TopicChangeType or missing Topic");
        }
    }

    public void HandleStreamChanged(StreamChangedNotification notification)
    {
        switch (notification.ChangeType)
        {
            case StreamChangeType.Started:
                // Try to resume stream for this topic if we have it
                // Don't dispatch StreamStarted here - TryResumeStreamAsync will do it
                var topic = topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == notification.TopicId);
                if (topic is not null && !streamingStore.State.ResumingTopics.Contains(notification.TopicId))
                {
                    _ = streamResumeService.TryResumeStreamAsync(topic);
                }
                else
                {
                    // Topic not found or already resuming - just update store state
                    dispatcher.Dispatch(new StreamStarted(notification.TopicId));
                }

                break;
            case StreamChangeType.Completed:
                dispatcher.Dispatch(new StreamCompleted(notification.TopicId));
                break;
            case StreamChangeType.Cancelled:
                dispatcher.Dispatch(new StreamCancelled(notification.TopicId));
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(notification),
                    notification.ChangeType,
                    "Invalid StreamChangeType");
        }
    }

    public void HandleApprovalResolved(ApprovalResolvedNotification notification)
    {
        dispatcher.Dispatch(new ApprovalResolved(notification.ApprovalId, notification.ToolCalls));

        if (!string.IsNullOrEmpty(notification.ToolCalls))
        {
            dispatcher.Dispatch(new StreamChunk(
                notification.TopicId,
                Content: null,
                Reasoning: null,
                ToolCalls: notification.ToolCalls,
                MessageId: null));
        }
    }

    public void HandleToolCalls(ToolCallsNotification notification)
    {
        dispatcher.Dispatch(new StreamChunk(
            notification.TopicId,
            Content: null,
            Reasoning: null,
            ToolCalls: notification.ToolCalls,
            MessageId: null));
    }

    public void HandleUserMessage(UserMessageNotification notification)
    {
        // Only add if we're watching this topic
        var currentTopic = topicsStore.State.SelectedTopicId;
        if (currentTopic != notification.TopicId)
        {
            return;
        }

        // Skip if this message was sent by this browser instance
        // (we already added it locally in SendMessageEffect)
        if (pipeline.WasSentByThisClient(notification.CorrelationId))
        {
            return;
        }

        // If streaming is active, finalize current assistant content before adding user message
        // This is the authoritative place to add OTHER users' messages because:
        // 1. We have correlationId to check if this client sent it (stream chunks don't have this)
        // 2. We can finalize streaming content with proper message ID for deduplication
        var streamingState = streamingStore.State;
        if (streamingState.StreamingTopics.Contains(notification.TopicId))
        {
            var currentContent = streamingState.StreamingByTopic.GetValueOrDefault(notification.TopicId);
            if (currentContent?.HasContent == true)
            {
                // Finalize current streaming content via pipeline
                pipeline.FinalizeMessage(notification.TopicId, currentContent.CurrentMessageId);
            }
        }

        // Add the user message
        dispatcher.Dispatch(new AddMessage(notification.TopicId, new ChatMessageModel
        {
            Role = "user",
            Content = notification.Content,
            SenderId = notification.SenderId,
            Timestamp = notification.Timestamp
        }));
    }
}