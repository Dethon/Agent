using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Calendar;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerCalendar.McpTools;

[McpServerToolType]
public class McpEventListTool(ICalendarProvider provider)
    : EventListTool(provider)
{
    [McpServerTool(Name = ToolName)]
    [Description(ToolDescription)]
    public async Task<CallToolResult> McpRun(
        [Description("Access token for calendar authentication")]
        string accessToken,
        [Description("Start date in ISO 8601 format")]
        string startDate,
        [Description("End date in ISO 8601 format")]
        string endDate,
        [Description("Optional calendar ID to filter events")]
        string? calendarId = null,
        CancellationToken cancellationToken = default)
    {
        var start = DateTimeOffset.Parse(startDate);
        var end = DateTimeOffset.Parse(endDate);
        var result = await Run(accessToken, start, end, calendarId, cancellationToken);
        return ToolResponse.Create(result);
    }
}
