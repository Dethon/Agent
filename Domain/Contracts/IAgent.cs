using Domain.DTOs;

namespace Domain.Contracts;

public interface IAgent
{
    public List<Message> Messages { get; }
    IAsyncEnumerable<AgentResponse> Run(string userPrompt, CancellationToken cancellationToken = default);
}