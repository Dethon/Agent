using System.Text.Json;
using Domain.Channels;
using Domain.DTOs.Channel;

namespace Domain.Tools.Channels;

// The transport half of channel_receive, shared by every channel server: each one exposes it
// through a thin Mcp* wrapper in its own McpTools folder (the tool's name lives in
// ChannelProtocol.ReceiveTool next to the rest of the wire contract), so channel servers that
// need nothing else from Infrastructure can reference Domain alone.
public class ChannelReceiveTool(ChannelInbox inbox)
{
    protected const string Description = "Internal channel transport. Long-polls for inbound channel items.";
    protected const string SubscriberIdDescription = "Stable subscriber id, e.g. channel-signalr";
    protected const string MaxWaitMsDescription = "How long to hold the request open, in milliseconds";

    // Clamped, not just conventionally short today: ChannelInbox.LiveSubscriberFreshness
    // gives a subscriber headroom for one fully held poll plus one retry backoff on the
    // assumption that no single poll holds the request open longer than DefaultReceiveWaitMs
    // (a subscriber is stamped when its poll *starts* — see ChannelInbox.Subscriber's own doc
    // comment). An unclamped maxWaitMs from a future/misbehaving caller could park a genuinely
    // live subscriber past the freshness window, making HasLiveSubscriber read it as dead —
    // worse than the bug the freshness check exists to fix.
    protected async Task<string> Run(string subscriberId, int maxWaitMs, CancellationToken cancellationToken)
    {
        var clampedWaitMs = Math.Clamp(maxWaitMs, 0, ChannelProtocol.DefaultReceiveWaitMs);
        var items = await inbox.ReceiveAsync(
            subscriberId, TimeSpan.FromMilliseconds(clampedWaitMs), cancellationToken);

        var payload = JsonSerializer.Serialize(
            new ChannelReceiveResult { Items = items }, ChannelProtocol.SerializerOptions);

        // The drain emptied the queue and the poll acknowledges nothing, so from here the batch
        // exists only in this response: a request aborted while it is being written takes the batch
        // with it. Asking the token once more with the payload in hand covers everything up to the
        // write, and a batch that has nowhere to go is handed back to the front of the queue for
        // the next poll. The write itself stays open — that needs an acknowledgement the protocol
        // does not have — but the wide part of the window, waiting on the wait, is closed.
        if (items.Count > 0 && cancellationToken.IsCancellationRequested)
        {
            inbox.Restore(subscriberId, items);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return payload;
    }
}