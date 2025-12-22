using System.Text.Json;
using Domain.Agents;

namespace Domain.Contracts;

public interface IThreadStateStore
{
    Task<JsonElement?> LoadAsync(AgentKey key, CancellationToken ct);
    Task SaveAsync(AgentKey key, JsonElement serializedThread, CancellationToken ct);
    Task DeleteAsync(AgentKey key, CancellationToken ct);
}