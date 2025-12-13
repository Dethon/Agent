using Microsoft.Agents.AI;

namespace Domain.Agents;

public abstract class DisposableAgent : AIAgent, IAsyncDisposable
{
    public abstract ValueTask DisposeAsync();
    public abstract ValueTask DisposeThreadSessionAsync(AgentThread thread);
}