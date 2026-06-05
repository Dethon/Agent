using System.Security.Cryptography;
using System.Text;
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
            CancellationToken ct) =>
        {
            if (!settings.Enabled)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var token = ctx.Request.Headers["X-Announce-Token"].FirstOrDefault();
            if (!TokenMatches(settings.Token, token))
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(body.Text) || body.Text.Length > settings.MaxTextLength)
            {
                return Results.BadRequest(new { error = $"Text must be between 1 and {settings.MaxTextLength} characters." });
            }

            if (body.Voice is not null && !VoiceId().IsMatch(body.Voice))
            {
                return Results.BadRequest(new { error = "Voice must contain only letters, digits, '-' or '_'." });
            }

            if (!HasTarget(body.Target))
            {
                return Results.BadRequest(new { error = "Target must specify exactly one of satelliteId, room, or all." });
            }

            try
            {
                var response = await announcer.AnnounceAsync(body, ct);
                return Results.Accepted(value: response);
            }
            catch (AnnounceTargetNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });
    }

    // Constant-time comparison so a wrong token cannot be recovered byte-by-byte via response timing.
    private static bool TokenMatches(string configured, string? provided)
    {
        if (string.IsNullOrEmpty(configured) || provided is null)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(configured));
    }

    private static bool HasTarget(AnnounceTarget target) =>
        !string.IsNullOrWhiteSpace(target.SatelliteId)
        || !string.IsNullOrWhiteSpace(target.Room)
        || target.All == true;

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial Regex VoiceId();
}