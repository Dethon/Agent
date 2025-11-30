using Domain.DTOs;

namespace Domain.Contracts;

public interface IAgent : IAsyncDisposable
{
    Task Run(string[] prompts, CancellationToken ct);
    Task Run(AiMessage[] prompts, CancellationToken ct);
}