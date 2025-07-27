using Domain.DTOs;

namespace Domain.Contracts;

public interface IAgent
{
    IAsyncEnumerable<AgentResponse> Run(
        string? prompt, bool cancelCurrentOperation, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AgentResponse> Run(
        Message[] prompts, bool cancelCurrentOperation, CancellationToken cancellationToken = default);
}