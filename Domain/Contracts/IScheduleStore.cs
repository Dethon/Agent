using Domain.DTOs;

namespace Domain.Contracts;

public interface IScheduleStore
{
    Task<Schedule> CreateAsync(Schedule schedule, CancellationToken ct = default);
    Task<Schedule?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<Schedule>> ListAsync(CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<Schedule>> GetDueSchedulesAsync(DateTime asOf, CancellationToken ct = default);
    Task UpdateLastRunAsync(string id, DateTime lastRunAt, DateTime? nextRunAt, CancellationToken ct = default);
}
