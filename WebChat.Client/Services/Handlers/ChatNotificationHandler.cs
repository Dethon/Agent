using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;

namespace WebChat.Client.Services.Handlers;

public sealed class ChatNotificationHandler(
    IChatStateManager stateManager,
    ITopicService topicService,
    StreamResumeService streamResumeService) : IChatNotificationHandler
{
    public Task HandleTopicChangedAsync(TopicChangedNotification notification)
    {
        switch (notification.ChangeType)
        {
            case TopicChangeType.Created when notification.Topic is not null:
                if (stateManager.Topics.All(t => t.TopicId != notification.TopicId))
                {
                    var newTopic = StoredTopic.FromMetadata(notification.Topic);
                    stateManager.AddTopic(newTopic);
                    _ = LoadTopicHistoryAsync(newTopic);
                }

                break;

            case TopicChangeType.Updated when notification.Topic is not null:
                stateManager.UpdateTopic(notification.Topic);
                break;

            case TopicChangeType.Deleted:
                stateManager.RemoveTopic(notification.TopicId);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(notification.ChangeType), notification.ChangeType, "ChangeType not recognized");
        }

        return Task.CompletedTask;
    }

    public async Task HandleStreamChangedAsync(StreamChangedNotification notification)
    {
        switch (notification.ChangeType)
        {
            case StreamChangeType.Started:
                var topic = stateManager.GetTopicById(notification.TopicId);
                if (topic is not null && !stateManager.IsTopicResuming(notification.TopicId))
                {
                    await streamResumeService.TryResumeStreamAsync(topic);
                }

                break;

            case StreamChangeType.Cancelled:
            case StreamChangeType.Completed:
                stateManager.StopStreaming(notification.TopicId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(notification.ChangeType), notification.ChangeType, "");
        }
    }

    public async Task HandleNewMessageAsync(NewMessageNotification notification)
    {
        var topic = stateManager.GetTopicById(notification.TopicId);
        if (topic is not null && !stateManager.IsTopicStreaming(notification.TopicId))
        {
            var history = await topicService.GetHistoryAsync(topic.ChatId, topic.ThreadId);
            var messages = history.Select(h => new ChatMessageModel
            {
                Role = h.Role,
                Content = h.Content
            }).ToList();
            stateManager.SetMessagesForTopic(topic.TopicId, messages);
        }
    }

    public Task HandleApprovalResolvedAsync(ApprovalResolvedNotification notification)
    {
        if (stateManager.CurrentApprovalRequest?.ApprovalId == notification.ApprovalId)
        {
            stateManager.SetApprovalRequest(null);
        }

        if (!string.IsNullOrEmpty(notification.ToolCalls))
        {
            stateManager.AddToolCallsToStreamingMessage(notification.TopicId, notification.ToolCalls);
        }

        return Task.CompletedTask;
    }

    public Task HandleToolCallsAsync(ToolCallsNotification notification)
    {
        stateManager.AddToolCallsToStreamingMessage(notification.TopicId, notification.ToolCalls);
        return Task.CompletedTask;
    }

    private async Task LoadTopicHistoryAsync(StoredTopic topic)
    {
        if (!stateManager.HasMessagesForTopic(topic.TopicId))
        {
            var history = await topicService.GetHistoryAsync(topic.ChatId, topic.ThreadId);
            var messages = history.Select(h => new ChatMessageModel
            {
                Role = h.Role,
                Content = h.Content
            }).ToList();
            stateManager.SetMessagesForTopic(topic.TopicId, messages);
        }

        await streamResumeService.TryResumeStreamAsync(topic);
    }
}