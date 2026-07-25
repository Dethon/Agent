using Domain.DTOs.Voice;

namespace Domain.Contracts;

// Fires an insistent alert (repeats until acknowledged or capped) on the target satellites. The
// in-process implementation lives in the voice hub; the timers server reaches it over HTTP.
public interface IInsistentAnnouncer
{
    Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct);
}