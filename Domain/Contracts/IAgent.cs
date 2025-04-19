using Domain.DTOs;

namespace Domain.Contracts;

public interface IAgent
{
    Task<List<Message>> Run(string userPrompt, CancellationToken cancellationToken = default);
}