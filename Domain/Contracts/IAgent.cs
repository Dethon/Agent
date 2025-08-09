using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IAgent
{
    Task Run(string? prompt, CancellationToken ct);
    Task Run(ChatMessage[] prompts, CancellationToken ct);
    void CancelCurrentExecution(bool keepListening = false);
}