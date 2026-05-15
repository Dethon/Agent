using Microsoft.Agents.AI;

namespace Domain.Agents;

public abstract class DisposableAgent : AIAgent, IAsyncDisposable
{
    public abstract ValueTask DisposeAsync();
    public abstract ValueTask DisposeThreadSessionAsync(AgentSession thread);

    // Optional: pre-initialize the per-conversation session (MCP connections + tool
    // discovery) so that setup overlaps with first-message handling instead of
    // blocking the first LLM turn. No-op for agents without expensive session setup.
    public virtual Task WarmupSessionAsync(AgentSession thread, CancellationToken ct = default)
        => Task.CompletedTask;
}