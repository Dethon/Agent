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
                dispatcher.Dispatch(new StreamStarted(notification.TopicId));
                // Try to resume stream for this topic if we have it
                var topic = topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == notification.TopicId);
                if (topic is not null && !streamingStore.State.ResumingTopics.Contains(notification.TopicId))
                {
                    _ = streamResumeService.TryResumeStreamAsync(topic);
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
}
