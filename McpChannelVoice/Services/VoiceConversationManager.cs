using Domain.Contracts;
using Domain.DTOs.Channel;

namespace McpChannelVoice.Services;

// One active conversation per satellite. Each utterance renews a TimeProvider-based
// idle timer; when it fires, the in-memory mapping is dropped so the next utterance
// mints a fresh conversation. Persisted Redis history and the WebChat topic are left
// intact (the timer only clears local routing state).
public sealed class VoiceConversationManager(
    IConversationFactory factory,
    ReplyTextAccumulator accumulator,
    TimeProvider time,
    TimeSpan lifetime,
    ILogger<VoiceConversationManager> logger)
{
    private sealed record Entry(string ConversationId, ITimer Timer);

    private readonly Dictionary<string, Entry> _bySatellite = new();
    private readonly Dictionary<string, string> _conversationToSatellite = new();
    private readonly Lock _gate = new();

    // firstUtterance only seeds the new conversation's WebChat topic (its InitialPrompt);
    // the caller still dispatches the utterance itself via the channel using the returned id.
    public async Task<string> GetOrCreateAsync(
        SatelliteSession session, string agentId, string firstUtterance, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_bySatellite.TryGetValue(session.SatelliteId, out var existing))
            {
                existing.Timer.Change(lifetime, Timeout.InfiniteTimeSpan);
                return existing.ConversationId;
            }
        }

        var creation = await factory.CreateAsync(
            new CreateConversationParams
            {
                AgentId = agentId,
                TopicName = $"{session.Config.Identity} @ {session.Config.Room}",
                Sender = session.Config.Identity,
                InitialPrompt = firstUtterance
            },
            ct);

        lock (_gate)
        {
            if (_bySatellite.TryGetValue(session.SatelliteId, out var existing))
            {
                existing.Timer.Change(lifetime, Timeout.InfiniteTimeSpan);
                return existing.ConversationId;
            }

            var satelliteId = session.SatelliteId;
            var timer = time.CreateTimer(_ => Expire(satelliteId), null, lifetime, Timeout.InfiniteTimeSpan);
            _bySatellite[satelliteId] = new Entry(creation.Identity.ConversationId, timer);
            _conversationToSatellite[creation.Identity.ConversationId] = satelliteId;
            logger.LogInformation(
                "Voice conversation {ConversationId} opened for satellite {Satellite}",
                creation.Identity.ConversationId, satelliteId);
            return creation.Identity.ConversationId;
        }
    }

    public string? GetActiveConversationId(string satelliteId)
    {
        lock (_gate)
        {
            return _bySatellite.TryGetValue(satelliteId, out var entry) ? entry.ConversationId : null;
        }
    }

    public string? ResolveSatelliteId(string conversationId)
    {
        lock (_gate)
        {
            return _conversationToSatellite.GetValueOrDefault(conversationId);
        }
    }

    private void Expire(string satelliteId)
    {
        lock (_gate)
        {
            if (!_bySatellite.Remove(satelliteId, out var entry))
            {
                return;
            }

            _conversationToSatellite.Remove(entry.ConversationId);
            entry.Timer.Dispose();
            accumulator.Flush(entry.ConversationId);
            logger.LogInformation(
                "Voice conversation {ConversationId} expired for satellite {Satellite}",
                entry.ConversationId, satelliteId);
        }
    }
}