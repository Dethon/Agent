using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Calendar;

public class EventListTool(ICalendarProvider provider)
{
    public const string ToolName = "event_list";
    public const string ToolDescription = """
        Lists calendar events within a date range.
        Optionally filters by calendar ID. Returns event summaries.
        """;

    protected async Task<JsonNode> Run(
        string accessToken,
        DateTimeOffset start,
        DateTimeOffset end,
        string? calendarId = null,
        CancellationToken ct = default)
    {
        var events = await provider.ListEventsAsync(accessToken, calendarId, start, end, ct);
        return new JsonArray(events.Select(CalendarEventMapper.ToJsonObject).Cast<JsonNode>().ToArray());
    }
}
