using Domain.DTOs;

namespace Domain.Contracts;

public interface IAgent
{
    DateTime LastExecutionTime { get; }
    Task Run(string[] prompts, CancellationToken ct);
    Task Run(ChatMessage[] prompts, CancellationToken ct);
    void CancelCurrentExecution();
}