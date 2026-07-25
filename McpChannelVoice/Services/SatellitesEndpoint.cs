using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

// The out-of-process satellite catalog: the timers server fetches the roster to describe targets and
// POSTs targets here to resolve them. Resolution stays hub-authoritative — the same SatelliteRegistry
// that fires announcements resolves create-time timer targets, so the two can never disagree.
public static class SatellitesEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/voice/satellites", (HttpContext ctx, AnnounceSettings settings, SatelliteRegistry registry) =>
            VoiceHubAuth.Reject(ctx, settings) ?? Results.Ok(registry.GetAll()));

        app.MapPost("/api/voice/satellites/resolve",
            (AnnounceTarget target, HttpContext ctx, AnnounceSettings settings, SatelliteRegistry registry) =>
                VoiceHubAuth.Reject(ctx, settings) ?? Results.Ok(registry.Resolve(target)));
    }
}