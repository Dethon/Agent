using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools;

public abstract record AgentToolParams
{
    public abstract Message[] ToMessages();
}

public abstract class BaseAgentTool<TSelf, TParams>(Func<IAgent> agentFactory) : BaseTool<TSelf, TParams>
    where TSelf : IToolWithMetadata where TParams : AgentToolParams
{
    protected async Task<AgentResponse> GetAgentResponse(TParams parameters,
        CancellationToken cancellationToken = default)
    {
        var agent = agentFactory();
        var responses = agent.Run(parameters.ToMessages(), cancellationToken);
        return await responses.LastAsync(cancellationToken);
    }
}

public abstract class BaseAgentTool<TSelf>(Func<IAgent> agentFactory) : BaseTool<TSelf> where TSelf : IToolWithMetadata
{
    protected async Task<AgentResponse> GetAgentResponse(CancellationToken cancellationToken = default)
    {
        var agent = agentFactory();
        var responses = agent.Run([], cancellationToken);
        return await responses.LastAsync(cancellationToken);
    }
}