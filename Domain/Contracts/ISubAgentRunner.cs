using Domain.DTOs;

namespace Domain.Contracts;

public interface ISubAgentRunner
{
    Task<string> RunAsync(
        SubAgentDefinition definition,
        string prompt,
        SubAgentContext parentContext,
        CancellationToken ct = default);
}
