using Domain.DTOs;

namespace Domain.Contracts;

public interface IAgent
{
    IAsyncEnumerable<AgentResponse> Run(string userPrompt, CancellationToken cancellationToken = default);
}