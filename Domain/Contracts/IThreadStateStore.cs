using Domain.Agents;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IThreadStateStore
{
    Task DeleteAsync(AgentKey key);
    Task<ChatMessage[]?> GetMessagesAsync(string key);
    Task SetMessagesAsync(string key, ChatMessage[] messages);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync();
    Task SaveTopicAsync(TopicMetadata topic);
    Task DeleteTopicAsync(string topicId);
    Task<TopicMetadata?> GetTopicByChatIdAndThreadIdAsync(long chatId, long threadId, CancellationToken ct = default);
}