using Domain.Agents;

namespace Domain.DTOs;

public record FeatureConfig(
    IReadOnlySet<string>? EnabledTools = null,
    Func<SubAgentDefinition, DisposableAgent>? SubAgentFactory = null);