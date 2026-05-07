using Domain.Agents;
using Domain.Contracts;

namespace Domain.DTOs;

public record FeatureConfig(
    IReadOnlySet<string>? EnabledTools = null,
    Func<SubAgentDefinition, DisposableAgent>? SubAgentFactory = null,
    string? UserId = null,
    ISubAgentSessions? SubAgentSessions = null);