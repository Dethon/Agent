using Domain.DTOs.WebChat;

namespace WebChat.Client.Contracts;

public interface ITopicService
{
    Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync();
    Task SaveTopicAsync(TopicMetadata topic, bool isNew = false);
    Task DeleteTopicAsync(string topicId, long chatId, long threadId);
    Task<IReadOnlyList<ChatHistoryMessage>> GetHistoryAsync(long chatId, long threadId);
}