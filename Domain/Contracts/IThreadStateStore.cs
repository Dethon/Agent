using Domain.Agents;
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IThreadStateStore
{
    Task DeleteAsync(AgentKey key);
    Task<string?> GetMessagesAsync(string key);
    Task SetMessagesAsync(string key, string json, TimeSpan expiry);
    Task<IReadOnlyList<ChatMessage>?> GetChatHistoryAsync(AgentKey key);
}