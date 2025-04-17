using Domain.DTOs;

namespace Domain.Contracts;

public interface IAgent
{
    Task<AgentResponse> Run(string userPrompt, CancellationToken cancellationToken = default);
}