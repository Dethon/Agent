using Domain.DTOs.Voice;
using McpChannelVoice.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace McpChannelVoice.Services;

public static class AnnounceEndpoint
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
            if (string.IsNullOrEmpty(settings.Token) || token != settings.Token)
            {
                return Results.Unauthorized();
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
}