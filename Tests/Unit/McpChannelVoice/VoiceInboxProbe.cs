using Channels.Hosting;
using Domain.Channels;
using Domain.DTOs.Channel;

namespace Tests.Unit.McpChannelVoice;

// A real ChannelInbox behind the real emitter, drained the way the agent's channel connection
// drains it. This replaces the emitter subclass the voice tests used to override, which was a test
// seam carried in production code and which asserted against a substitute for the delivery path
// rather than the path itself.
//
// Safe to emit into from two satellite connections at once: the inbox is thread-safe and the
// accumulated snapshot is guarded, so a concurrency defect fails the assertion it belongs to
// instead of corrupting a list.
internal sealed class VoiceInboxProbe
{
    private const string Subscriber = ChannelProtocol.ChannelClientNamePrefix + "voice";

    private readonly ChannelInbox _inbox = new();
    private readonly Lock _gate = new();
    private readonly List<ChannelMessageNotification> _messages = [];

    public VoiceInboxProbe()
    {
        // Registers the subscriber before anything can be emitted. Broadcast only reaches
        // subscribers that already exist, so a probe that registered lazily would miss the first
        // message and read as a delivery failure.
        Drain(_inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult());
        Emitter = new ChannelNotificationEmitter(_inbox, DeliveryPolicy.Broadcast);
    }

    public ChannelNotificationEmitter Emitter { get; }

    public IReadOnlyList<ChannelMessageNotification> Messages
    {
        get
        {
            Drain(_inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult());
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }

    public async Task<ChannelMessageNotification> FirstAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        await WaitForCountAsync(1, timeout, ct);
        return Messages[0];
    }

    // A real long poll against the inbox, so the wait ends when the item actually lands rather
    // than on a sleep.
    public async Task WaitForCountAsync(int expected, TimeSpan timeout, CancellationToken ct = default)
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        while (Messages.Count < expected && !linked.IsCancellationRequested)
        {
            try
            {
                Drain(await _inbox.ReceiveAsync(Subscriber, TimeSpan.FromMilliseconds(250), linked.Token));
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var received = Messages.Count;
        if (received < expected)
        {
            throw new TimeoutException(
                $"Expected {expected} voice notification(s) within {timeout}, received {received}.");
        }
    }

    private void Drain(IReadOnlyList<ChannelInboxItem> batch)
    {
        var messages = batch
            .Where(item => item.Kind == ChannelInboxItemKind.Message)
            .Select(item => item.Message!)
            .ToArray();
        if (messages.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _messages.AddRange(messages);
        }
    }
}