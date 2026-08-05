using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class TopicService(IChatLiveConnection liveConnection) : ITopicService
{
    public Task<HubResult<IReadOnlyList<TopicMetadata>>> GetAllTopicsAsync(
        string agentId, string spaceSlug = "default") =>
        liveConnection.InvokeAsync<IReadOnlyList<TopicMetadata>>("GetAllTopics", agentId, spaceSlug);

    public Task<HubResult<Nothing>> SaveTopicAsync(TopicMetadata topic, bool isNew = false) =>
        liveConnection.InvokeAsync("SaveTopic", topic, isNew);

    public Task<HubResult<Nothing>> DeleteTopicAsync(string agentId, string topicId, long chatId, long threadId) =>
        liveConnection.InvokeAsync("DeleteTopic", agentId, topicId, chatId, threadId);

    public Task<HubResult<IReadOnlyList<ChatHistoryMessage>>> GetHistoryAsync(
        string agentId, long chatId, long threadId) =>
        liveConnection.InvokeAsync<IReadOnlyList<ChatHistoryMessage>>("GetHistory", agentId, chatId, threadId);

    public Task<HubResult<Nothing>> JoinSpaceAsync(string spaceSlug) =>
        liveConnection.InvokeAsync("JoinSpace", spaceSlug);
}