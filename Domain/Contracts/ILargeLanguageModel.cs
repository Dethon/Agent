using Domain.DTOs;
using ModelContextProtocol.Client;

namespace Domain.Contracts;

public interface ILargeLanguageModel
{
    Task<AgentResponse[]> Prompt(
        IEnumerable<Message> messages,
        IEnumerable<McpClientTool> tools,
        float? temperature = null,
        CancellationToken cancellationToken = default);
}