using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Calendar;

public class EventUpdateTool(ICalendarProvider provider)
{
    public const string ToolName = "event_update";
    public const string ToolDescription = """
        Updates an existing calendar event with patch semantics.
        Only the fields provided in the request are updated. Returns the updated event.
        """;

    protected async Task<JsonNode> Run(
        string accessToken,
        string eventId,
        EventUpdateRequest request,
        CancellationToken ct = default)
    {
        var updatedEvent = await provider.UpdateEventAsync(accessToken, eventId, request, ct);
        return CalendarEventMapper.ToJsonObject(updatedEvent);
    }
}
