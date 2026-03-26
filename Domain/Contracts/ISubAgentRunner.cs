using Domain.DTOs;

namespace Domain.Contracts;

public interface ISubAgentRunner
{
    Task<string> RunAsync(
        SubAgentDefinition definition,
        string prompt,
        FeatureConfig parentContext,
        CancellationToken ct = default);
}
