using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public sealed class RedisScheduleStore(IConnectionMultiplexer redis) : IScheduleStore
{
    private const string ScheduleSetKey = "schedules";
    private const string DueSetKey = "schedules:due";
    private static readonly TimeSpan _oneShotBuffer = TimeSpan.FromHours(1);

    private readonly IDatabase _db = redis.GetDatabase();

    public async Task<Schedule> CreateAsync(Schedule schedule, CancellationToken ct = default)
    {
        var key = ScheduleKey(schedule.Id);
        var json = JsonSerializer.Serialize(schedule);

        var transaction = _db.CreateTransaction();

        _ = transaction.StringSetAsync(key, json);
        _ = transaction.SetAddAsync(ScheduleSetKey, schedule.Id);

        if (schedule.NextRunAt.HasValue)
        {
            _ = transaction.SortedSetAddAsync(DueSetKey, schedule.Id, schedule.NextRunAt.Value.Ticks);
        }

        if (schedule.CronExpression is null && schedule.RunAt.HasValue)
        {
            _ = transaction.KeyExpireAsync(key, schedule.RunAt.Value.Add(_oneShotBuffer) - DateTime.UtcNow);
        }

        await transaction.ExecuteAsync();

        return schedule;
    }

    public async Task<Schedule?> GetAsync(string id, CancellationToken ct = default)
    {
        var json = await _db.StringGetAsync(ScheduleKey(id));
        return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<Schedule>(json.ToString());
    }

    public async Task<IReadOnlyList<Schedule>> ListAsync(CancellationToken ct = default)
    {
        var ids = await _db.SetMembersAsync(ScheduleSetKey);
        var schedules = new List<Schedule>();

        foreach (var id in ids)
        {
            var schedule = await GetAsync(id!, ct);
            if (schedule is not null)
            {
                schedules.Add(schedule);
            }
        }

        return schedules.OrderBy(s => s.NextRunAt ?? DateTime.MaxValue).ToList();
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var transaction = _db.CreateTransaction();

        _ = transaction.KeyDeleteAsync(ScheduleKey(id));
        _ = transaction.SetRemoveAsync(ScheduleSetKey, id);
        _ = transaction.SortedSetRemoveAsync(DueSetKey, id);

        await transaction.ExecuteAsync();
    }

    public async Task<IReadOnlyList<Schedule>> GetDueSchedulesAsync(DateTime asOf, CancellationToken ct = default)
    {
        var dueIds = await _db.SortedSetRangeByScoreAsync(
            DueSetKey,
            stop: asOf.Ticks,
            take: 100);

        var schedules = new List<Schedule>();

        foreach (var id in dueIds)
        {
            var schedule = await GetAsync(id!, ct);
            if (schedule is not null)
            {
                schedules.Add(schedule);
            }
        }

        return schedules;
    }

    public async Task UpdateLastRunAsync(string id, DateTime lastRunAt, DateTime? nextRunAt,
        CancellationToken ct = default)
    {
        var schedule = await GetAsync(id, ct);
        if (schedule is null)
        {
            return;
        }

        var updated = schedule with
        {
            LastRunAt = lastRunAt,
            NextRunAt = nextRunAt
        };

        var json = JsonSerializer.Serialize(updated);

        var transaction = _db.CreateTransaction();

        _ = transaction.StringSetAsync(ScheduleKey(id), json);

        if (nextRunAt.HasValue)
        {
            _ = transaction.SortedSetAddAsync(DueSetKey, id, nextRunAt.Value.Ticks);
        }
        else
        {
            _ = transaction.SortedSetRemoveAsync(DueSetKey, id);
        }

        await transaction.ExecuteAsync();
    }

    private static string ScheduleKey(string id) => $"schedule:{id}";
}