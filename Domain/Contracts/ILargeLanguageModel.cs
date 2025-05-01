using Domain.DTOs;

namespace Domain.Contracts;

public interface ILargeLanguageModel
{
    Task<AgentResponse[]> Prompt(
        IEnumerable<Message> messages,
        IEnumerable<ToolDefinition> tools,
        bool enableSearch = false,
        float? temperature = null,
        CancellationToken cancellationToken = default);
}