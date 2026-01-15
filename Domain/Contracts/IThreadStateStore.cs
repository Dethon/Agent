using Domain.Agents;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IThreadStateStore
{
    Task DeleteAsync(AgentKey key);
    ChatMessage[]? GetMessages(string key);
    Task SetMessagesAsync(string key, ChatMessage[] messages);

    Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync();
    Task SaveTopicAsync(TopicMetadata topic);
    Task DeleteTopicAsync(string topicId);
}