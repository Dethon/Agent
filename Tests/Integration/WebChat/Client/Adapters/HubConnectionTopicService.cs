using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR.Client;
using WebChat.Client.Contracts;

namespace Tests.Integration.WebChat.Client.Adapters;

public sealed class HubConnectionTopicService(HubConnection connection) : ITopicService
{
    public async Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync(string agentId)
    {
        return await connection.InvokeAsync<IReadOnlyList<TopicMetadata>>("GetAllTopics", agentId, "default");
    }

    public async Task SaveTopicAsync(TopicMetadata topic, bool isNew = false)
    {
        await connection.InvokeAsync("SaveTopic", topic, isNew);
    }

    public async Task DeleteTopicAsync(string agentId, string topicId, long chatId, long threadId)
    {
        await connection.InvokeAsync("DeleteTopic", agentId, topicId, chatId, threadId);
    }

    public async Task<IReadOnlyList<ChatHistoryMessage>> GetHistoryAsync(string agentId, long chatId, long threadId)
    {
        return await connection.InvokeAsync<IReadOnlyList<ChatHistoryMessage>>("GetHistory", agentId, chatId, threadId);
    }

    public async Task<bool> StartSessionAsync(string agentId, string topicId, long chatId, long threadId)
    {
        return await connection.InvokeAsync<bool>("StartSession", agentId, topicId, chatId, threadId);
    }
}