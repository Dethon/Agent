using Domain.DTOs.WebChat;

namespace WebChat.Client.Contracts;

public interface IChatNotificationHandler
{
    Task HandleTopicChangedAsync(TopicChangedNotification notification);
    Task HandleStreamChangedAsync(StreamChangedNotification notification);
    Task HandleNewMessageAsync(NewMessageNotification notification);
    Task HandleApprovalResolvedAsync(ApprovalResolvedNotification notification);
    Task HandleToolCallsAsync(ToolCallsNotification notification);
}