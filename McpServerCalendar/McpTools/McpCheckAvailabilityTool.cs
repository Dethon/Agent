using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Calendar;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerCalendar.McpTools;

[McpServerToolType]
public class McpCheckAvailabilityTool(ICalendarProvider provider)
    : CheckAvailabilityTool(provider)
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
        CancellationToken cancellationToken = default)
    {
        var start = DateTimeOffset.Parse(startDate);
        var end = DateTimeOffset.Parse(endDate);
        var result = await Run(accessToken, start, end, cancellationToken);
        return ToolResponse.Create(result);
    }
}
