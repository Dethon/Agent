using Domain.Contracts;
using Domain.DTOs;
using McpServerScheduling.Settings;

namespace McpServerScheduling.Services;

public sealed class ScheduleAgentCatalog(SchedulingSettings settings) : IScheduleAgentCatalog
{
    private readonly IReadOnlyList<ScheduleAgentInfo> _agents = settings.Agents
        .Select(a => new ScheduleAgentInfo(a.Id, a.Name, a.Description))
        .ToList();

    public IReadOnlyList<ScheduleAgentInfo> GetAll() => _agents;
    public ScheduleAgentInfo? Get(string agentId) => _agents.FirstOrDefault(a => a.Id == agentId);
    public bool Exists(string agentId) => _agents.Any(a => a.Id == agentId);
}