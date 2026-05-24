using Domain.DTOs;

namespace Domain.Contracts;

public interface IScheduleAgentCatalog
{
    IReadOnlyList<ScheduleAgentInfo> GetAll();
    ScheduleAgentInfo? Get(string agentId);
    bool Exists(string agentId);
}