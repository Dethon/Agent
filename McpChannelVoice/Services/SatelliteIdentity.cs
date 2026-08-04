using Domain.DTOs.Metrics;

namespace McpChannelVoice.Services;

// Which satellite something is about, said once. Every report the hub makes names a satellite by
// these three fields together, so a caller that says which satellite it means cannot name two of
// them and forget the last.
public readonly record struct SatelliteIdentity(string SatelliteId, string? Room, string? Identity)
{
    public static SatelliteIdentity Of(SatelliteSession session) =>
        new(session.SatelliteId, session.Config.Room, session.Config.Identity);
}

// Stamping lives here rather than on VoiceEvent because the event is a Domain DTO and must not
// learn what a satellite session is. The voice server knows both.
public static class VoiceEventIdentity
{
    public static VoiceEvent About(this VoiceEvent evt, SatelliteIdentity identity) => evt with
    {
        SatelliteId = identity.SatelliteId,
        Room = identity.Room,
        Identity = identity.Identity
    };

    public static VoiceEvent About(this VoiceEvent evt, SatelliteSession session) =>
        evt.About(SatelliteIdentity.Of(session));
}