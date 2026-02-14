using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Calendar;

public class CalendarListTool(ICalendarProvider provider)
{
    public const string ToolName = "calendar_list";
    public const string ToolDescription = """
        Lists all calendars available for the authenticated user.
        Returns calendar IDs, names, whether each is the default, and edit permissions.
        """;

    protected async Task<JsonNode> Run(string accessToken, CancellationToken ct = default)
    {
        var calendars = await provider.ListCalendarsAsync(accessToken, ct);
        return new JsonArray(calendars.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id,
            ["name"] = c.Name,
            ["isDefault"] = c.IsDefault,
            ["canEdit"] = c.CanEdit,
            ["color"] = c.Color
        }).ToArray());
    }
}
