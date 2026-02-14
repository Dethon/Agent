using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Calendar;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerCalendar.McpTools;

[McpServerToolType]
public class McpEventGetTool(ICalendarProvider provider)
    : EventGetTool(provider)
{
    [McpServerTool(Name = ToolName)]
    [Description(ToolDescription)]
    public async Task<CallToolResult> McpRun(
        [Description("Access token for calendar authentication")]
        string accessToken,
        [Description("Event ID to retrieve")]
        string eventId,
        [Description("Optional calendar ID")]
        string? calendarId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await Run(accessToken, eventId, calendarId, cancellationToken);
        return ToolResponse.Create(result);
    }
}
