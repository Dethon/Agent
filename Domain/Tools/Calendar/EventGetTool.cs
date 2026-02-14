using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Calendar;

public class EventGetTool(ICalendarProvider provider)
{
    public const string ToolName = "event_get";
    public const string ToolDescription = """
        Retrieves a single calendar event by its ID.
        Returns all event fields including attendees, recurrence, and organizer.
        """;

    protected async Task<JsonNode> Run(
        string accessToken,
        string eventId,
        string? calendarId = null,
        CancellationToken ct = default)
    {
        var calendarEvent = await provider.GetEventAsync(accessToken, eventId, calendarId, ct);
        return CalendarEventMapper.ToJsonObject(calendarEvent);
    }
}
