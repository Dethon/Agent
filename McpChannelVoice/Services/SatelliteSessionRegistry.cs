using System.Collections.Concurrent;

namespace McpChannelVoice.Services;

public sealed class SatelliteSessionRegistry
{
    private readonly ConcurrentDictionary<string, SatelliteSession> _sessions = new();

    public void Register(SatelliteSession session) => _sessions[session.SatelliteId] = session;

    public void Unregister(string satelliteId) => _sessions.TryRemove(satelliteId, out _);

    public SatelliteSession? Get(string satelliteId) =>
        _sessions.TryGetValue(satelliteId, out var s) ? s : null;

    public IReadOnlyList<SatelliteSession> All() => _sessions.Values.ToList();
}