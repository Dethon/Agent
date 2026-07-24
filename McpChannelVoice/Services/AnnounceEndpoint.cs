using System.Text.RegularExpressions;
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public static partial class AnnounceEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/voice/announce", async (
            AnnounceRequest body,
            HttpContext ctx,
            AnnounceSettings settings,
            AnnouncementService announcer,
            InsistentAnnouncementController insistent) =>
        {
            if (VoiceHubAuth.Reject(ctx, settings) is { } rejection)
            {
                return rejection;
            }

            if (string.IsNullOrWhiteSpace(body.Text) || body.Text.Length > settings.MaxTextLength)
            {
                return Results.BadRequest(new { error = $"Text must be between 1 and {settings.MaxTextLength} characters." });
            }

            if (body.Voice is not null && !VoiceId().IsMatch(body.Voice))
            {
                return Results.BadRequest(new { error = "Voice must contain only letters, digits, '-' or '_'." });
            }

            if (body.Target is null || !HasTarget(body.Target))
            {
                return Results.BadRequest(new { error = "Target must specify at least one of satelliteId, satelliteIds, room, or all." });
            }

            try
            {
                // Synthesis and playback run on the satellite's background playback loop, which
                // outlives this HTTP request, so the job runs detached (see CancellationToken.None).
                var response = body.Insistent is not null
                    ? await insistent.StartAsync(body, CancellationToken.None)
                    : await announcer.AnnounceAsync(body, CancellationToken.None);
                return Results.Accepted(value: response);
            }
            catch (AnnounceTargetNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });
    }

    private static bool HasTarget(AnnounceTarget target) =>
        !string.IsNullOrWhiteSpace(target.SatelliteId)
        || target.SatelliteIds is { Count: > 0 }
        || !string.IsNullOrWhiteSpace(target.Room)
        || target.All == true;

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex VoiceId();
}