using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Hub;

public interface IHubEventDispatcher
{
    void HandleTopicChanged(TopicChangedNotification notification);
    void HandleStreamChanged(StreamChangedNotification notification);
    void HandleNewMessage(NewMessageNotification notification);
    void HandleApprovalResolved(ApprovalResolvedNotification notification);
    void HandleToolCalls(ToolCallsNotification notification);
}