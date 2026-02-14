using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Calendar;

public class EventDeleteTool(ICalendarProvider provider)
{
    public const string ToolName = "event_delete";
    public const string ToolDescription = """
        Deletes a calendar event by its ID.
        Returns a confirmation with the deleted event ID.
        """;

    protected async Task<JsonNode> Run(
        string accessToken,
        string eventId,
        string? calendarId = null,
        CancellationToken ct = default)
    {
        await provider.DeleteEventAsync(accessToken, eventId, calendarId, ct);
        return new JsonObject
        {
            ["status"] = "deleted",
            ["eventId"] = eventId
        };
    }
}
