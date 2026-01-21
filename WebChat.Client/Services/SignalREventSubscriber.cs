using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;
using WebChat.Client.State.Hub;

namespace WebChat.Client.Services;

public sealed class SignalREventSubscriber(
    ChatConnectionService connectionService,
    IHubEventDispatcher hubEventDispatcher) : ISignalREventSubscriber
{
    private readonly List<IDisposable> _subscriptions = new();
    private bool _disposed;

    public bool IsSubscribed { get; private set; }

    public void Subscribe()
    {
        if (IsSubscribed || _disposed)
        {
            return;
        }

        var hubConnection = connectionService.HubConnection;
        if (hubConnection is null)
        {
            return;
        }

        _subscriptions.Add(
            hubConnection.On<TopicChangedNotification>("OnTopicChanged", notification =>
            {
                hubEventDispatcher.HandleTopicChanged(notification);
            }));

        _subscriptions.Add(
            hubConnection.On<StreamChangedNotification>("OnStreamChanged", notification =>
            {
                hubEventDispatcher.HandleStreamChanged(notification);
            }));

        _subscriptions.Add(
            hubConnection.On<NewMessageNotification>("OnNewMessage", notification =>
            {
                hubEventDispatcher.HandleNewMessage(notification);
            }));

        _subscriptions.Add(
            hubConnection.On<ApprovalResolvedNotification>("OnApprovalResolved", notification =>
            {
                hubEventDispatcher.HandleApprovalResolved(notification);
            }));

        _subscriptions.Add(
            hubConnection.On<ToolCallsNotification>("OnToolCalls", notification =>
            {
                hubEventDispatcher.HandleToolCalls(notification);
            }));

        IsSubscribed = true;
    }

    public void Unsubscribe()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();
        IsSubscribed = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Unsubscribe();
        _disposed = true;
    }
}
