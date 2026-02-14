using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Calendar;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerCalendar.McpTools;

[McpServerToolType]
public class McpCalendarListTool(ICalendarProvider provider)
    : CalendarListTool(provider)
{
    [McpServerTool(Name = ToolName)]
    [Description(ToolDescription)]
    public async Task<CallToolResult> McpRun(
        [Description("Access token for calendar authentication")]
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var result = await Run(accessToken, cancellationToken);
        return ToolResponse.Create(result);
    }
}
