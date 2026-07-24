using Domain.DTOs.Voice;

namespace Domain.Contracts;

// Silences every insistent alert currently ringing on the voice satellites — the agent-reachable
// "stop" for rings that are otherwise only dismissable by waking a targeted satellite. Async: the
// ringing state lives in the voice hub, which the timers server reaches over HTTP.
public interface IAlertDismisser
{
    Task<IReadOnlyList<DismissedAlert>> DismissAllAsync(CancellationToken ct);
}