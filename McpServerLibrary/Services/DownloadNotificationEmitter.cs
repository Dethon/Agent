using Domain.Channels;
using Domain.DTOs.Channel;

namespace McpServerLibrary.Services;

public sealed class DownloadNotificationEmitter(ChannelInbox inbox) : IDownloadNotificationEmitter
{
    // Only the agent's channel connection ever calls channel_receive on this dual-role server —
    // tool sessions (per-conversation filesystem clients) never poll it, so no client-name filter
    // is needed here: the distinction is structural now, not something the emitter enforces.
    public bool HasActiveSessions => inbox.HasSubscribers;

    public Task<bool> EmitAsync(ChannelMessageNotification payload, CancellationToken ct = default)
    {
        if (!inbox.HasSubscribers)
        {
            return Task.FromResult(false);
        }

        inbox.Enqueue(ChannelInboxItem.ForMessage(payload));
        return Task.FromResult(true);
    }
}