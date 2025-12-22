using Domain.Agents;

namespace Domain.Contracts;

public interface IThreadStateStore
{
    Task DeleteAsync(AgentKey key);
    Task<string?> GetMessagesAsync(string key);
    Task SetMessagesAsync(string key, string json, TimeSpan expiry);
}