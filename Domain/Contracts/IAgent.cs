using Domain.DTOs;

namespace Domain.Contracts;

public interface IAgent : IAsyncDisposable
{
    DateTime LastExecutionTime { get; }
    Task Run(string[] prompts, CancellationToken ct);
    Task Run(AiMessage[] prompts, CancellationToken ct);
    void CancelCurrentExecution();
}