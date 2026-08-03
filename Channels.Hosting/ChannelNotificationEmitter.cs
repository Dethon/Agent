using Domain.Channels;
using Domain.DTOs.Channel;

namespace Channels.Hosting;

// The one way a channel server puts an item on the wire. Emitting answers "was anyone listening?"
// as its return value, with the freshness check inside the operation, so a caller can neither skip
// the question nor ask it a different way — which is how the same stale-subscriber defect landed
// independently in six hand-copied emitters.
//
// Sealed on purpose: substituting it in a test replaces the delivery path with an override of it.
// Tests construct a real ChannelInbox and drain it instead.
public sealed class ChannelNotificationEmitter
{
    private readonly ChannelInbox _inbox;
    private readonly DeliveryPolicy _policy;
    private readonly string? _subscriberId;

    public ChannelNotificationEmitter(ChannelInbox inbox, DeliveryPolicy policy, string? subscriberId = null)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        ChannelDelivery.ValidateSubscriberId(policy, subscriberId);
        _inbox = inbox;
        _policy = policy;
        _subscriberId = subscriberId;
    }

    public DeliveryPolicy Policy => _policy;

    public Task<bool> EmitAsync(ChannelMessageNotification payload, CancellationToken ct = default) =>
        Task.FromResult(Deliver(ChannelInboxItem.ForMessage(payload)));

    public Task<bool> EmitCancelAsync(ChannelCancelNotification payload, CancellationToken ct = default) =>
        Task.FromResult(Deliver(ChannelInboxItem.ForCancel(payload)));

    private bool Deliver(ChannelInboxItem item)
    {
        var live = _inbox.HasLiveSubscriber();
        switch (_policy)
        {
            case DeliveryPolicy.GateOnLive when !live:
                return false;
            case DeliveryPolicy.BufferAlways:
                _inbox.EnqueueFor(_subscriberId!, item);
                break;
            default:
                _inbox.Enqueue(item);
                break;
        }

        return live;
    }
}