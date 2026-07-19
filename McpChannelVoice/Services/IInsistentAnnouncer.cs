using Domain.DTOs.Voice;

namespace McpChannelVoice.Services;

// Test seam over InsistentAnnouncementController for in-process callers (TimerFireService).
public interface IInsistentAnnouncer
{
    Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct);
}