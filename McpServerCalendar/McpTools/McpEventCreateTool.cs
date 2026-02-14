using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Calendar;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerCalendar.McpTools;

[McpServerToolType]
public class McpEventCreateTool(ICalendarProvider provider)
    : EventCreateTool(provider)
{
    [McpServerTool(Name = ToolName)]
    [Description(ToolDescription)]
    public async Task<CallToolResult> McpRun(
        [Description("Access token for calendar authentication")]
        string accessToken,
        [Description("Event subject/title")]
        string subject,
        [Description("Start date in ISO 8601 format")]
        string startDate,
        [Description("End date in ISO 8601 format")]
        string endDate,
        [Description("Optional calendar ID")]
        string? calendarId = null,
        [Description("Optional location")]
        string? location = null,
        [Description("Optional body/description")]
        string? body = null,
        [Description("Optional comma-separated list of attendee email addresses")]
        string? attendees = null,
        [Description("Whether this is an all-day event")]
        bool? isAllDay = null,
        [Description("Optional recurrence pattern")]
        string? recurrence = null,
        CancellationToken cancellationToken = default)
    {
        var request = new EventCreateRequest
        {
            Subject = subject,
            Start = DateTimeOffset.Parse(startDate),
            End = DateTimeOffset.Parse(endDate),
            CalendarId = calendarId,
            Location = location,
            Body = body,
            Attendees = attendees?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            IsAllDay = isAllDay,
            Recurrence = recurrence
        };

        var result = await Run(accessToken, request, cancellationToken);
        return ToolResponse.Create(result);
    }
}
