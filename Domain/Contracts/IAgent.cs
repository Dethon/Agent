using Domain.DTOs;

namespace Domain.Contracts;

public interface IAgent
{
    IAsyncEnumerable<AgentResponse> Run(string prompt, CancellationToken cancellationToken = default);
}