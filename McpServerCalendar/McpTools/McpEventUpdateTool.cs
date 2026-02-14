using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Calendar;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerCalendar.McpTools;

[McpServerToolType]
public class McpEventUpdateTool(ICalendarProvider provider)
    : EventUpdateTool(provider)
{
    [McpServerTool(Name = ToolName)]
    [Description(ToolDescription)]
    public async Task<CallToolResult> McpRun(
        [Description("Access token for calendar authentication")]
        string accessToken,
        [Description("Event ID to update")]
        string eventId,
        [Description("Optional new subject/title")]
        string? subject = null,
        [Description("Optional new start date in ISO 8601 format")]
        string? startDate = null,
        [Description("Optional new end date in ISO 8601 format")]
        string? endDate = null,
        [Description("Optional new location")]
        string? location = null,
        [Description("Optional new body/description")]
        string? body = null,
        [Description("Optional comma-separated list of attendee email addresses")]
        string? attendees = null,
        [Description("Whether this is an all-day event")]
        bool? isAllDay = null,
        [Description("Optional new recurrence pattern")]
        string? recurrence = null,
        CancellationToken cancellationToken = default)
    {
        var request = new EventUpdateRequest
        {
            Subject = subject,
            Start = startDate is not null ? DateTimeOffset.Parse(startDate) : null,
            End = endDate is not null ? DateTimeOffset.Parse(endDate) : null,
            Location = location,
            Body = body,
            Attendees = attendees?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            IsAllDay = isAllDay,
            Recurrence = recurrence
        };

        var result = await Run(accessToken, eventId, request, cancellationToken);
        return ToolResponse.Create(result);
    }
}
