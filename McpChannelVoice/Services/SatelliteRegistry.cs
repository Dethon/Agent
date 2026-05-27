using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed class SatelliteRegistry
{
    private readonly IReadOnlyDictionary<string, SatelliteConfig> _byId;
    private readonly ILookup<string, string> _idsByRoom;

    public SatelliteRegistry(IReadOnlyDictionary<string, SatelliteConfig> satellites)
    {
        _byId = satellites;
        _idsByRoom = satellites
            .ToLookup(kv => kv.Value.Room, kv => kv.Key, StringComparer.OrdinalIgnoreCase);
    }

    public SatelliteConfig? GetById(string satelliteId) =>
        _byId.TryGetValue(satelliteId, out var cfg) ? cfg : null;

    public IReadOnlyList<string> GetIdsByRoom(string room) =>
        _idsByRoom[room].ToList();

    public IReadOnlyList<string> GetAllIds() =>
        _byId.Keys.ToList();
}