using Domain.DTOs;

namespace Domain.Contracts;

using ResponseCallback = Func<AiResponse, CancellationToken, Task>;

public interface IAgentFactory
{
    Task<IAgent> Create(ResponseCallback responseCallback, CancellationToken cancellationToken);
}