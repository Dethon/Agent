using Microsoft.Agents.AI;

namespace Domain.Agents;

public abstract class DisposableAgent : AIAgent, IAsyncDisposable
{
    public abstract ValueTask DisposeAsync();
    public abstract ValueTask DisposeThreadSessionAsync(AgentSession thread);

    public virtual ValueTask WarmupSessionAsync(AgentSession thread, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}