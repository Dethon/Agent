using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed class SatelliteRegistry
{
    private readonly IReadOnlyDictionary<string, SatelliteConfig> _byId;
    private readonly ILookup<string, string> _idsByRoom;

    public SatelliteRegistry(IReadOnlyDictionary<string, SatelliteConfig> satellites)
    {
        _byId = satellites;
        // Both the bare Room and the DisplayLocation ("Kitchen (Madrid, Spain)") route. The agent is
        // only ever shown DisplayLocation — by the satellite catalog prompt and by the per-message
        // header — so keying on Room alone made every room target copied from either one resolve to
        // nothing, and the announcement silently never played.
        _idsByRoom = satellites
            .SelectMany(kv => new[] { kv.Value.Room, kv.Value.DisplayLocation }
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(room => (Room: room, Id: kv.Key)))
            .ToLookup(x => x.Room, x => x.Id, StringComparer.OrdinalIgnoreCase);
    }

    public SatelliteConfig? GetById(string satelliteId) =>
        _byId.TryGetValue(satelliteId, out var cfg) ? cfg : null;

    public IReadOnlyList<string> GetIdsByRoom(string room) =>
        _idsByRoom[room].ToList();

    public IReadOnlyList<string> GetAllIds() =>
        _byId.Keys.ToList();

    public bool Exists(string satelliteId) => GetById(satelliteId) is not null;

    public IReadOnlyList<SatelliteDescriptor> GetAll() =>
        _byId.Select(kv => new SatelliteDescriptor(kv.Key, kv.Value.Room)).ToList();

    // The single source of AnnounceTarget precedence: announcements, insistent alerts and
    // create-time timer validation all route through here, so a target accepted at create time
    // is exactly a target that will ring.
    public IReadOnlyList<string> Resolve(AnnounceTarget target)
    {
        if (target.SatelliteIds is { Count: > 0 })
        {
            // SatelliteIds comes from LLM-authored JSON where a null element is expressible, and
            // Dictionary.TryGetValue(null) throws.
            return target.SatelliteIds.Where(id => id is not null && Exists(id)).Distinct().ToList();
        }
        if (target.SatelliteId is not null)
        {
            return Exists(target.SatelliteId) ? [target.SatelliteId] : [];
        }
        if (target.Room is not null)
        {
            return GetIdsByRoom(target.Room);
        }
        return target.All == true ? GetAllIds() : [];
    }
}