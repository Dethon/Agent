using Domain.DTOs.WebChat;

namespace WebChat.Client.Contracts;

public interface ITopicService
{
    Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync(string agentId, string spaceSlug = "default");
    Task SaveTopicAsync(TopicMetadata topic, bool isNew = false);
    Task DeleteTopicAsync(string agentId, string topicId, long chatId, long threadId);
    Task<IReadOnlyList<ChatHistoryMessage>> GetHistoryAsync(string agentId, long chatId, long threadId);
    Task<string?> JoinSpaceAsync(string spaceSlug);
}