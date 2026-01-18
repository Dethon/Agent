using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class SignalREventSubscriber(
    ChatConnectionService connectionService,
    IChatNotificationHandler notificationHandler)
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

        hubConnection.On<TopicChangedNotification>("OnTopicChanged", async notification =>
        {
            await notificationHandler.HandleTopicChangedAsync(notification);
        });

        hubConnection.On<StreamChangedNotification>("OnStreamChanged", async notification =>
        {
            await notificationHandler.HandleStreamChangedAsync(notification);
        });

        hubConnection.On<NewMessageNotification>("OnNewMessage", async notification =>
        {
            await notificationHandler.HandleNewMessageAsync(notification);
        });

        hubConnection.On<ApprovalResolvedNotification>("OnApprovalResolved", async notification =>
        {
            await notificationHandler.HandleApprovalResolvedAsync(notification);
        });

        hubConnection.On<ToolCallsNotification>("OnToolCalls", async notification =>
        {
            await notificationHandler.HandleToolCallsAsync(notification);
        });

        _subscribed = true;
    }
}