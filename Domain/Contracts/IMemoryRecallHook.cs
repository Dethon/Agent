using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IMemoryRecallHook
{
    Task EnrichAsync(
        ChatMessage message,
        string userId,
        string? conversationId,
        string? agentId,
        AgentSession thread,
        CancellationToken ct);
}
