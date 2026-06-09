using Domain.DTOs.Voice;

namespace McpChannelVoice.Services;

// Maps a schedule-minted conversation id to the satellite selector its reply should be
// spoken on. Populated by the voice create_conversation tool; resolved by send_reply on
// stream-complete. Bindings are removed when the reply is delivered; the idle timer is a
// backstop that drops a binding if the agent run dies before completing.
public sealed class VoiceDeliveryRegistry(
    TimeProvider time,
    TimeSpan lifetime,
    ReplyTextAccumulator accumulator,
    ILogger<VoiceDeliveryRegistry> logger)
{
    private sealed record Entry(AnnounceTarget Target, ITimer Timer, long Generation);

    private readonly Dictionary<string, Entry> _byConversation = new();
    private readonly Lock _gate = new();
    private long _generation;

    public void Bind(string conversationId, AnnounceTarget target)
    {
        lock (_gate)
        {
            if (_byConversation.Remove(conversationId, out var existing))
            {
                existing.Timer.Dispose();
            }

            var generation = ++_generation;
            var timer = time.CreateTimer(_ => Expire(conversationId, generation), null, lifetime, Timeout.InfiniteTimeSpan);
            _byConversation[conversationId] = new Entry(target, timer, generation);
        }
    }

    public AnnounceTarget? Resolve(string conversationId)
    {
        lock (_gate)
        {
            return _byConversation.TryGetValue(conversationId, out var entry) ? entry.Target : null;
        }
    }

    public void Remove(string conversationId)
    {
        lock (_gate)
        {
            if (_byConversation.Remove(conversationId, out var entry))
            {
                entry.Timer.Dispose();
            }
        }
    }

    private void Expire(string conversationId, long generation)
    {
        lock (_gate)
        {
            // Only expire if this is still the timer that armed it: a re-Bind during the callback
            // installs a fresh entry/timer with a newer generation that must not be dropped here.
            if (_byConversation.TryGetValue(conversationId, out var entry) && entry.Generation == generation)
            {
                _byConversation.Remove(conversationId);
                entry.Timer.Dispose();
                // Drop any buffered reply text for an abandoned scheduled delivery so it doesn't
                // leak in the singleton accumulator (mirrors VoiceConversationManager.Expire).
                accumulator.Flush(conversationId);
                logger.LogInformation("Voice delivery binding {ConversationId} expired", conversationId);
            }
        }
    }
}