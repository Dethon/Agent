using Domain.DTOs;

namespace Domain.Contracts;

public interface ILargeLanguageModel
{
    Task<AgentResponse[]> Prompt(
        IEnumerable<Message> messages,
        IEnumerable<ToolDefinition> tools,
        CancellationToken cancellationToken = default);
}