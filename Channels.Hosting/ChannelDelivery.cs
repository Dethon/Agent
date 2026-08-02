namespace Channels.Hosting;

internal static class ChannelDelivery
{
    // Checked where the policy is chosen rather than at first emit. A buffer-always id that does
    // not match what the agent's channel connection derives buffers into a queue nobody drains,
    // and nothing reports it — the failure is otherwise completely silent.
    public static void ValidateSubscriberId(DeliveryPolicy policy, string? subscriberId)
    {
        if (policy == DeliveryPolicy.BufferAlways && string.IsNullOrWhiteSpace(subscriberId))
        {
            throw new ArgumentException(
                $"{nameof(DeliveryPolicy.BufferAlways)} targets a known subscriber and requires a subscriber id.",
                nameof(subscriberId));
        }

        if (policy != DeliveryPolicy.BufferAlways && subscriberId is not null)
        {
            throw new ArgumentException(
                $"{policy} enqueues to whoever is registered and must not be given a subscriber id.",
                nameof(subscriberId));
        }
    }
}