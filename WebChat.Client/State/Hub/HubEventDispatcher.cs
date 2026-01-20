using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Hub;

public sealed class HubEventDispatcher(
    IDispatcher dispatcher,
    TopicsStore topicsStore,
    StreamingStore streamingStore,
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

    public void HandleNewMessage(NewMessageNotification notification)
    {
        dispatcher.Dispatch(new LoadMessages(notification.TopicId));
    }

    public void HandleApprovalResolved(ApprovalResolvedNotification notification)
    {
        dispatcher.Dispatch(new ApprovalResolved(notification.ApprovalId, notification.ToolCalls));

        if (!string.IsNullOrEmpty(notification.ToolCalls))
        {
            // Accumulate tool calls since reducer replaces (StreamingService sends accumulated content)
            var accumulatedToolCalls = AccumulateToolCalls(notification.TopicId, notification.ToolCalls);
            dispatcher.Dispatch(new StreamChunk(
                notification.TopicId,
                Content: null,
                Reasoning: null,
                ToolCalls: accumulatedToolCalls,
                MessageId: null));
        }
    }

    public void HandleToolCalls(ToolCallsNotification notification)
    {
        // Accumulate tool calls since reducer replaces (StreamingService sends accumulated content)
        var accumulatedToolCalls = AccumulateToolCalls(notification.TopicId, notification.ToolCalls);
        dispatcher.Dispatch(new StreamChunk(
            notification.TopicId,
            Content: null,
            Reasoning: null,
            ToolCalls: accumulatedToolCalls,
            MessageId: null));
    }

    private string AccumulateToolCalls(string topicId, string newToolCalls)
    {
        var existing = streamingStore.State.StreamingByTopic.GetValueOrDefault(topicId);
        return string.IsNullOrEmpty(existing?.ToolCalls)
            ? newToolCalls
            : existing.ToolCalls + "\n" + newToolCalls;
    }
}
