using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace WebChat.Client.Services.Handlers;

public sealed class ChatNotificationHandler(
    IDispatcher dispatcher,
    TopicsStore topicsStore,
    StreamingStore streamingStore,
    ApprovalStore approvalStore,
    ITopicService topicService,
    StreamResumeService streamResumeService) : IChatNotificationHandler
{
    public Task HandleTopicChangedAsync(TopicChangedNotification notification)
    {
        switch (notification.ChangeType)
        {
            case TopicChangeType.Created when notification.Topic is not null:
                if (topicsStore.State.Topics.All(t => t.TopicId != notification.TopicId))
                {
                    var newTopic = StoredTopic.FromMetadata(notification.Topic);
                    dispatcher.Dispatch(new AddTopic(newTopic));
                }

                break;

            case TopicChangeType.Updated when notification.Topic is not null:
                if (topicsStore.State.Topics.All(t => t.TopicId != notification.TopicId))
                {
                    var newTopic = StoredTopic.FromMetadata(notification.Topic);
                    dispatcher.Dispatch(new AddTopic(newTopic));
                }
                else
                {
                    var existingTopic = topicsStore.State.Topics
                        .First(t => t.TopicId == notification.TopicId);
                    var updatedTopic = existingTopic.ApplyMetadata(notification.Topic);
                    dispatcher.Dispatch(new UpdateTopic(updatedTopic));
                }

                break;

            case TopicChangeType.Deleted:
                dispatcher.Dispatch(new RemoveTopic(notification.TopicId));
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(notification),
                    notification,
                    "Cannot handle topic change notification");
        }

        return Task.CompletedTask;
    }

    public Task HandleStreamChangedAsync(StreamChangedNotification notification)
    {
        switch (notification.ChangeType)
        {
            case StreamChangeType.Started:
                var topic = topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == notification.TopicId);
                if (topic is not null && !streamingStore.State.ResumingTopics.Contains(notification.TopicId))
                {
                    // Don't await - run in background to avoid blocking SignalR message processing
                    _ = streamResumeService.TryResumeStreamAsync(topic);
                }

                break;

            case StreamChangeType.Cancelled:
            case StreamChangeType.Completed:
                dispatcher.Dispatch(new StreamCompleted(notification.TopicId));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(notification.ChangeType), notification.ChangeType, "");
        }

        return Task.CompletedTask;
    }

    public Task HandleNewMessageAsync(NewMessageNotification notification)
    {
        var topic = topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == notification.TopicId);
        if (topic is not null && !streamingStore.State.StreamingTopics.Contains(notification.TopicId))
        {
            // Don't await - run in background to avoid blocking SignalR message processing
            _ = LoadMessagesForTopicAsync(topic);
        }

        return Task.CompletedTask;
    }

    private async Task LoadMessagesForTopicAsync(StoredTopic topic)
    {
        var history = await topicService.GetHistoryAsync(topic.ChatId, topic.ThreadId);
        var messages = history.Select(h => new ChatMessageModel
        {
            Role = h.Role,
            Content = h.Content
        }).ToList();
        dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, messages));
    }

    public Task HandleApprovalResolvedAsync(ApprovalResolvedNotification notification)
    {
        if (approvalStore.State.CurrentRequest?.ApprovalId == notification.ApprovalId)
        {
            dispatcher.Dispatch(new ClearApproval());
        }

        if (!string.IsNullOrEmpty(notification.ToolCalls))
        {
            dispatcher.Dispatch(new StreamChunk(notification.TopicId, null, null, notification.ToolCalls, null));
        }

        return Task.CompletedTask;
    }

    public Task HandleToolCallsAsync(ToolCallsNotification notification)
    {
        dispatcher.Dispatch(new StreamChunk(notification.TopicId, null, null, notification.ToolCalls, null));
        return Task.CompletedTask;
    }
}
