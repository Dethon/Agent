using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Calendar;

public class CheckAvailabilityTool(ICalendarProvider provider)
{
    public const string ToolName = "check_availability";
    public const string ToolDescription = """
        Checks the user's availability within a date range.
        Returns free/busy time slots with their status (Free, Busy, Tentative, OutOfOffice).
        """;

    protected async Task<JsonNode> Run(
        string accessToken,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct = default)
    {
        var slots = await provider.CheckAvailabilityAsync(accessToken, start, end, ct);
        return new JsonArray(slots.Select(s => (JsonNode)new JsonObject
        {
            ["start"] = s.Start.ToString("o"),
            ["end"] = s.End.ToString("o"),
            ["status"] = s.Status.ToString()
        }).ToArray());
    }
}
