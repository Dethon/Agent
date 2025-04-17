using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Agents;

public class DownloadAgent: IAgent
{
    public async Task<AgentResponse> Run(string userPrompt, CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(new AgentResponse
        {
            Answer = "Dummy response"
        });
    }
}