using Domain.Contracts;
using Domain.DTOs;

namespace Tests.Unit.Domain.Scheduling.Vfs;

public sealed class FakeAgentCatalog(IReadOnlyList<ScheduleAgentInfo> agents) : IScheduleAgentCatalog
{
    public IReadOnlyList<ScheduleAgentInfo> GetAll() => agents;
    public ScheduleAgentInfo? Get(string agentId) => agents.FirstOrDefault(a => a.Id == agentId);
    public bool Exists(string agentId) => agents.Any(a => a.Id == agentId);
}