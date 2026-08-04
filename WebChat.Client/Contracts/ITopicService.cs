using Domain.DTOs.WebChat;

namespace WebChat.Client.Contracts;

public interface ITopicService
{
    Task<HubResult<IReadOnlyList<TopicMetadata>>> GetAllTopicsAsync(string agentId, string spaceSlug = "default");
    Task<HubResult<Nothing>> SaveTopicAsync(TopicMetadata topic, bool isNew = false);
    Task<HubResult<Nothing>> DeleteTopicAsync(string agentId, string topicId, long chatId, long threadId);
    Task<HubResult<IReadOnlyList<ChatHistoryMessage>>> GetHistoryAsync(string agentId, long chatId, long threadId);
    Task JoinSpaceAsync(string spaceSlug);
}