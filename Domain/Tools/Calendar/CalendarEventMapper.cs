using System.Text.Json.Nodes;
using Domain.DTOs;

namespace Domain.Tools.Calendar;

internal static class CalendarEventMapper
{
    public static JsonObject ToJsonObject(CalendarEvent e) => new()
    {
        ["id"] = e.Id,
        ["calendarId"] = e.CalendarId,
        ["subject"] = e.Subject,
        ["body"] = e.Body,
        ["start"] = e.Start.ToString("o"),
        ["end"] = e.End.ToString("o"),
        ["location"] = e.Location,
        ["isAllDay"] = e.IsAllDay,
        ["recurrence"] = e.Recurrence,
        ["attendees"] = new JsonArray(e.Attendees.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
        ["organizer"] = e.Organizer,
        ["status"] = e.Status
    };
}
