using Domain.Agents;

namespace Domain.DTOs;

public record FeatureConfig(
    Func<SubAgentDefinition, DisposableAgent>? SubAgentFactory = null);
