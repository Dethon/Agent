using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class TopicService(ChatConnectionService connectionService) : ITopicService
{
    public async Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync()
    {
        var hubConnection = connectionService.HubConnection;
        if (hubConnection is null)
        {
            return [];
        }

        return await hubConnection.InvokeAsync<IReadOnlyList<TopicMetadata>>("GetAllTopics");
    }

    public async Task SaveTopicAsync(TopicMetadata topic, bool isNew = false)
    {
        var hubConnection = connectionService.HubConnection;
        if (hubConnection is null)
        {
            return;
        }

        await hubConnection.InvokeAsync("SaveTopic", topic, isNew);
    }

    public async Task DeleteTopicAsync(string topicId, long chatId, long threadId)
    {
        var hubConnection = connectionService.HubConnection;
        if (hubConnection is null)
        {
            return;
        }

        await hubConnection.InvokeAsync("DeleteTopic", topicId, chatId, threadId);
    }

    public async Task<IReadOnlyList<ChatHistoryMessage>> GetHistoryAsync(long chatId, long threadId)
    {
        var hubConnection = connectionService.HubConnection;
        if (hubConnection is null)
        {
            return [];
        }

        return await hubConnection.InvokeAsync<IReadOnlyList<ChatHistoryMessage>>("GetHistory", chatId, threadId);
    }
}