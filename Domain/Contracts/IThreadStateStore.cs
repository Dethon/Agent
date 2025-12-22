using Domain.Agents;

namespace Domain.Contracts;

public interface IThreadStateStore
{
    Task DeleteAsync(AgentKey key);
}