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
            hubConnection.On<TopicChangedNotification>(
                "OnTopicChanged", hubEventDispatcher.HandleTopicChanged));

        _subscriptions.Add(
            hubConnection.On<StreamChangedNotification>(
                "OnStreamChanged", hubEventDispatcher.HandleStreamChanged));

        _subscriptions.Add(
            hubConnection.On<ApprovalResolvedNotification>(
                "OnApprovalResolved", hubEventDispatcher.HandleApprovalResolved));

        _subscriptions.Add(
            hubConnection.On<ToolCallsNotification>(
                "OnToolCalls", hubEventDispatcher.HandleToolCalls));

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