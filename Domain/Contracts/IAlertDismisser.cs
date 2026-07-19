using Domain.DTOs.Voice;

namespace Domain.Contracts;

// Silences every insistent alert currently ringing on the voice satellites — the agent-reachable
// "stop" for rings that are otherwise only dismissable by waking a targeted satellite.
public interface IAlertDismisser
{
    IReadOnlyList<DismissedAlert> DismissAll();
}