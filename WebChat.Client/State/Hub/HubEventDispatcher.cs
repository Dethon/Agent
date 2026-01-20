using Domain.DTOs.WebChat;
using WebChat.Client.Models;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Hub;

public sealed class HubEventDispatcher(IDispatcher dispatcher) : IHubEventDispatcher
{
    public void HandleTopicChanged(TopicChangedNotification notification)
    {
        var action = notification.ChangeType switch
        {
            TopicChangeType.Created when notification.Topic is not null
                => (IAction)new AddTopic(StoredTopic.FromMetadata(notification.Topic)),
            TopicChangeType.Updated when notification.Topic is not null
                => new UpdateTopic(StoredTopic.FromMetadata(notification.Topic)),
            TopicChangeType.Deleted
                => new RemoveTopic(notification.TopicId),
            _ => throw new ArgumentOutOfRangeException(
                nameof(notification),
                notification.ChangeType,
                "Invalid TopicChangeType or missing Topic")
        };
        dispatcher.Dispatch(action);
    }

    public void HandleStreamChanged(StreamChangedNotification notification)
    {
        var action = notification.ChangeType switch
        {
            StreamChangeType.Started => (IAction)new StreamStarted(notification.TopicId),
            StreamChangeType.Completed => new StreamCompleted(notification.TopicId),
            StreamChangeType.Cancelled => new StreamCancelled(notification.TopicId),
            _ => throw new ArgumentOutOfRangeException(
                nameof(notification),
                notification.ChangeType,
                "Invalid StreamChangeType")
        };
        dispatcher.Dispatch(action);
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
