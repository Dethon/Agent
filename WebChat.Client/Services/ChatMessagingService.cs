using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class ChatMessagingService(ChatConnectionService connectionService) : IChatMessagingService
{
    public async IAsyncEnumerable<ChatStreamMessage> SendMessageAsync(string topicId, string message,
        string? correlationId = null)
    {
        var hubConnection = connectionService.HubConnection;
        if (hubConnection is null)
        {
            yield break;
        }

        var stream = hubConnection.StreamAsync<ChatStreamMessage>("SendMessage", topicId, message, correlationId);

        await foreach (var item in stream)
        {
            yield return item;
        }
    }

    public async IAsyncEnumerable<ChatStreamMessage> ResumeStreamAsync(string topicId)
    {
        var hubConnection = connectionService.HubConnection;
        if (hubConnection is null)
        {
            yield break;
        }

        var stream = hubConnection.StreamAsync<ChatStreamMessage>("ResumeStream", topicId);

        await foreach (var item in stream)
        {
            yield return item;
        }
    }

    public async Task<StreamState?> GetStreamStateAsync(string topicId)
    {
        var hubConnection = connectionService.HubConnection;
        if (hubConnection is null)
        {
            return null;
        }

        return await hubConnection.InvokeAsync<StreamState?>("GetStreamState", topicId);
    }

    public async Task CancelTopicAsync(string topicId)
    {
        var hubConnection = connectionService.HubConnection;
        if (hubConnection is null)
        {
            return;
        }

        await hubConnection.InvokeAsync("CancelTopic", topicId);
    }

    public async Task<bool> EnqueueMessageAsync(string topicId, string message, string? correlationId = null)
    {
        var hubConnection = connectionService.HubConnection;
        if (hubConnection is null)
        {
            return false;
        }

        return await hubConnection.InvokeAsync<bool>("EnqueueMessage", topicId, message, correlationId);
    }
}