using Domain.DTOs.Channel;

namespace McpServerScheduling.Services;

public interface IScheduleNotificationEmitter
{
    bool HasActiveSessions { get; }

    Task<bool> EmitAsync(ChannelMessageNotification payload, CancellationToken ct = default);
}