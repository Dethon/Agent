using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.State.Hub;

namespace WebChat.Client.Services;

public sealed class SignalREventSubscriber(
    ChatConnectionService connectionService,
    IHubEventDispatcher hubEventDispatcher)
{
    private bool _subscribed;

    public void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        var hubConnection = connectionService.HubConnection;
        if (hubConnection is null)
        {
            return;
        }

        hubConnection.On<TopicChangedNotification>("OnTopicChanged", notification =>
        {
            hubEventDispatcher.HandleTopicChanged(notification);
        });

        hubConnection.On<StreamChangedNotification>("OnStreamChanged", notification =>
        {
            hubEventDispatcher.HandleStreamChanged(notification);
        });

        hubConnection.On<NewMessageNotification>("OnNewMessage", notification =>
        {
            hubEventDispatcher.HandleNewMessage(notification);
        });

        hubConnection.On<ApprovalResolvedNotification>("OnApprovalResolved", notification =>
        {
            hubEventDispatcher.HandleApprovalResolved(notification);
        });

        hubConnection.On<ToolCallsNotification>("OnToolCalls", notification =>
        {
            hubEventDispatcher.HandleToolCalls(notification);
        });

        _subscribed = true;
    }
}
