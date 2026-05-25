using Domain.Contracts;
using Domain.DTOs;

namespace Tests.Unit.Domain.Scheduling.Vfs;

public sealed class FakeScheduleStore : IScheduleStore
{
    public readonly Dictionary<string, Schedule> Items = new();

    public Task<Schedule> CreateAsync(Schedule schedule, CancellationToken ct = default)
    {
        Items[schedule.Id] = schedule;
        return Task.FromResult(schedule);
    }

    public Task<Schedule?> GetAsync(string id, CancellationToken ct = default)
        => Task.FromResult(Items.GetValueOrDefault(id));

    public Task<IReadOnlyList<Schedule>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Schedule>>(Items.Values.ToList());

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        Items.Remove(id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Schedule>> GetDueSchedulesAsync(DateTime asOf, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Schedule>>(Items.Values.Where(s => s.NextRunAt <= asOf).ToList());

    public Task UpdateLastRunAsync(string id, DateTime? lastRunAt, DateTime? nextRunAt, CancellationToken ct = default)
    {
        if (Items.TryGetValue(id, out var s))
        {
            Items[id] = s with { LastRunAt = lastRunAt ?? s.LastRunAt, NextRunAt = nextRunAt };
        }

        return Task.CompletedTask;
    }
}