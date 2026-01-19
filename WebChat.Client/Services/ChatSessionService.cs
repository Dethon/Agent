using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;
using WebChat.Client.Models;

namespace WebChat.Client.Services;

public sealed class ChatSessionService(ChatConnectionService connectionService) : IChatSessionService
{
    public StoredTopic? CurrentTopic { get; private set; }

    public event Action? OnSessionChanged;

    public async Task<bool> StartSessionAsync(StoredTopic topic)
    {
        var hubConnection = connectionService.HubConnection;
        if (hubConnection is null)
        {
            return false;
        }

        var success = await hubConnection.InvokeAsync<bool>(
            "StartSession", topic.AgentId, topic.TopicId, topic.ChatId, topic.ThreadId);

        if (!success)
        {
            return false;
        }

        CurrentTopic = topic;
        OnSessionChanged?.Invoke();

        return true;
    }

    public void ClearSession()
    {
        CurrentTopic = null;
        OnSessionChanged?.Invoke();
    }
}