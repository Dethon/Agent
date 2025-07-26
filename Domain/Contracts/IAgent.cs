using Domain.DTOs;

namespace Domain.Contracts;

public interface IAgent
{
    IAsyncEnumerable<AgentResponse> Run(string? prompt, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AgentResponse> Run(Message[] prompts, CancellationToken cancellationToken = default);
}