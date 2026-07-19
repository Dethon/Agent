using Domain.DTOs.Voice;

namespace Domain.Contracts;

public interface ITimerStore
{
    Task ArmAsync(ArmedTimer timer, CancellationToken ct = default);
    Task<ArmedTimer?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<ArmedTimer>> ListAsync(CancellationToken ct = default);
    Task<bool> CancelAsync(string id, CancellationToken ct = default);
    // Atomically removes and returns every timer due as of asOfUtc (fire-once semantics).
    Task<IReadOnlyList<ArmedTimer>> TakeDueAsync(DateTime asOfUtc, CancellationToken ct = default);
}