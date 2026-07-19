using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs.Voice;

namespace Infrastructure.Timers;

// Timers are deliberately non-durable (kitchen-scale countdowns; spec defers durability), so a
// process-local map is the whole store.
public sealed class InMemoryTimerStore : ITimerStore
{
    private readonly ConcurrentDictionary<string, ArmedTimer> _timers = new();

    public Task ArmAsync(ArmedTimer timer, CancellationToken ct = default)
    {
        _timers[timer.Id] = timer;
        return Task.CompletedTask;
    }

    public Task<ArmedTimer?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_timers.GetValueOrDefault(id));

    public Task<IReadOnlyList<ArmedTimer>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ArmedTimer>>(
            _timers.Values.OrderBy(t => t.FiresAtUtc).ThenBy(t => t.Id, StringComparer.Ordinal).ToList());

    public Task<bool> CancelAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_timers.TryRemove(id, out _));

    public Task<IReadOnlyList<ArmedTimer>> TakeDueAsync(DateTime asOfUtc, CancellationToken ct = default)
    {
        var due = _timers.Values
            .Where(t => t.FiresAtUtc <= asOfUtc)
            .OrderBy(t => t.FiresAtUtc)
            .Where(t => _timers.TryRemove(t.Id, out _)) // atomic claim per timer
            .ToList();
        return Task.FromResult<IReadOnlyList<ArmedTimer>>(due);
    }
}