using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class TopicService(IChatLiveConnection liveConnection) : ITopicService
{
    public Task<HubResult<IReadOnlyList<TopicMetadata>>> GetAllTopicsAsync(
        string agentId, string spaceSlug = "default") =>
        liveConnection.InvokeAsync<IReadOnlyList<TopicMetadata>>("GetAllTopics", agentId, spaceSlug);

    public async Task SaveTopicAsync(TopicMetadata topic, bool isNew = false)
    {
        var hubConnection = liveConnection.HubConnection;
        if (hubConnection is null)
        {
            return;
        }

        await hubConnection.InvokeAsync("SaveTopic", topic, isNew);
    }

    public async Task DeleteTopicAsync(string agentId, string topicId, long chatId, long threadId)
    {
        var hubConnection = liveConnection.HubConnection;
        if (hubConnection is null)
        {
            return;
        }

        await hubConnection.InvokeAsync("DeleteTopic", agentId, topicId, chatId, threadId);
    }

    public Task<HubResult<IReadOnlyList<ChatHistoryMessage>>> GetHistoryAsync(
        string agentId, long chatId, long threadId) =>
        liveConnection.InvokeAsync<IReadOnlyList<ChatHistoryMessage>>("GetHistory", agentId, chatId, threadId);

    public async Task JoinSpaceAsync(string spaceSlug)
    {
        var hubConnection = liveConnection.HubConnection;
        if (hubConnection is null)
        {
            return;
        }

        await hubConnection.InvokeAsync("JoinSpace", spaceSlug);
    }
}