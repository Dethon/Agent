using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Calendar;

public class EventCreateTool(ICalendarProvider provider)
{
    public const string ToolName = "event_create";
    public const string ToolDescription = """
        Creates a new calendar event.
        Returns the created event with its server-assigned ID and all fields.
        """;

    protected async Task<JsonNode> Run(
        string accessToken,
        EventCreateRequest request,
        CancellationToken ct = default)
    {
        var createdEvent = await provider.CreateEventAsync(accessToken, request, ct);
        return CalendarEventMapper.ToJsonObject(createdEvent);
    }
}
