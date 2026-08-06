using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Hub;

public sealed class HubEventDispatcher(
    IDispatcher dispatcher,
    TopicsStore topicsStore,
    TopicStreams topicStreams,
    IMessagePipeline pipeline) : IHubEventDispatcher
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
                // Reporting the push is all this type does. Deciding whether to resume the
                // stream or just mark it started belongs to StreamResumeEffect — resuming is
                // a hub call, and a dispatcher that made one would depend on the services
                // that reach back through the live connection.
                dispatcher.Dispatch(new RemoteStreamStarted(notification.TopicId));
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
            AddToolCalls(notification.TopicId, notification.ToolCalls, notification.MessageId);
        }
    }

    public void HandleToolCalls(ToolCallsNotification notification) =>
        AddToolCalls(notification.TopicId, notification.ToolCalls, notification.MessageId);

    // Both pushes arrive on their own and either can land after the reply it belonged to has
    // ended. TopicStreams answers that once: a topic with no reply in flight takes nothing.
    private void AddToolCalls(string topicId, string toolCalls, string? messageId) =>
        topicStreams.Append(topicId, new ChatStreamMessage { ToolCalls = toolCalls, MessageId = messageId });

    public void HandleAgentsUpdated(IReadOnlyList<AgentCatalogEntry> agents)
    {
        dispatcher.Dispatch(new SetAgents(agents));
    }

    public void HandleUserMessage(UserMessageNotification notification)
    {
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

        // This is the authoritative place to add OTHER users' messages because we have the
        // correlationId to check whether this client sent it — stream chunks do not carry one.
        // The agent's half-written text is closed off first so the two do not merge into one
        // bubble; on a topic with no reply in flight that does nothing.
        topicStreams.FinalizeCurrent(notification.TopicId);

        dispatcher.Dispatch(new AddMessage(notification.TopicId, new ChatMessageModel
        {
            Role = "user",
            Content = notification.Content,
            SenderId = notification.SenderId,
            Timestamp = notification.Timestamp
        }));
    }
}