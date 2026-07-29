using Domain.Channels;
using Domain.DTOs.Channel;

namespace McpServerLibrary.Services;

public sealed class DownloadNotificationEmitter(ChannelInbox inbox) : IDownloadNotificationEmitter
{
    // Only the agent's channel connection ever calls channel_receive on this dual-role server —
    // tool sessions (per-conversation filesystem clients) never poll it, so no client-name filter
    // is needed here: the distinction is structural now, not something the emitter enforces.
    //
    // HasSubscribers is the wrong check for gating EmitAsync's delivery report: PruneIdle keeps a
    // subscriber that is buffering items alive for up to an hour so a channel outage survives, so a
    // disconnected-but-buffered subscriber would otherwise read as "delivered" here. This gates a
    // destructive action (DownloadCompletionWatcher drops the routing entry only when EmitAsync
    // returns true), so it needs "is someone actually polling", not "is there bookkeeping for this
    // id" — ~2x the long-poll ceiling keeps a mid-poll subscriber always counted while a
    // disconnected agent stops counting almost immediately.
    private static readonly TimeSpan LiveSubscriberFreshness =
        TimeSpan.FromMilliseconds(ChannelProtocol.DefaultReceiveWaitMs * 2);

    public bool HasActiveSessions => inbox.HasLiveSubscriber(LiveSubscriberFreshness);

    public Task<bool> EmitAsync(ChannelMessageNotification payload, CancellationToken ct = default)
    {
        if (!inbox.HasLiveSubscriber(LiveSubscriberFreshness))
        {
            return Task.FromResult(false);
        }

        inbox.Enqueue(ChannelInboxItem.ForMessage(payload));
        return Task.FromResult(true);
    }
}