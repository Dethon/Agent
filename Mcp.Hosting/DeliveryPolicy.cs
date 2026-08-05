namespace Mcp.Hosting;

// How a channel server buffers an outbound item when nobody is polling. Required at registration
// with no default: the difference between Broadcast and GateOnLive is exactly the no-live-subscriber
// case, and it previously existed only as a difference between which enqueue call a developer had
// copied from another server.
public enum DeliveryPolicy
{
    // Fan out to every registered subscriber, whatever its freshness, so a subscriber that is idle
    // but not yet pruned still receives the item and a brief agent gap does not lose it. With no
    // subscriber registered at all there is nobody to fan out to and the item is discarded. For
    // transports with no way to redeliver.
    Broadcast,

    // Enqueue targeted at a known subscriber id, creating that subscriber's queue on demand, so an
    // item arriving before the agent's first poll is buffered rather than fanned out to nobody. For
    // transports with no channel-level way to tell a sender to try again later.
    BufferAlways,

    // Enqueue only when a live subscriber exists; otherwise nothing is buffered at all. For callers
    // that delete or advance a durable record on a confirmed delivery — buffering on a failed emit
    // would keep the record *and* leave a duplicate behind, so the item fires twice.
    GateOnLive
}