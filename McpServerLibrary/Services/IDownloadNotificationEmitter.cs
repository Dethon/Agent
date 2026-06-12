using Domain.DTOs.Channel;

namespace McpServerLibrary.Services;

public interface IDownloadNotificationEmitter
{
    bool HasActiveSessions { get; }

    Task<bool> EmitAsync(ChannelMessageNotification payload, CancellationToken ct = default);
}