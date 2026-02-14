using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.Calendar;

public class MicrosoftGraphCalendarProvider(HttpClient httpClient) : ICalendarProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/me/calendars");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var graphResponse = await response.Content.ReadFromJsonAsync<GraphListResponse<GraphCalendar>>(JsonOptions, ct);
        return graphResponse!.Value.Select(MapToCalendarInfo).ToList();
    }

    public async Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(string accessToken, string? calendarId,
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
    {
        var path = calendarId is not null
            ? $"/me/calendars/{calendarId}/events"
            : "/me/events";

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var graphResponse = await response.Content.ReadFromJsonAsync<GraphListResponse<GraphEvent>>(JsonOptions, ct);
        return graphResponse!.Value.Select(MapToCalendarEvent).ToList();
    }

    public async Task<CalendarEvent> GetEventAsync(string accessToken, string eventId, string? calendarId,
        CancellationToken ct = default)
    {
        var path = calendarId is not null
            ? $"/me/calendars/{calendarId}/events/{eventId}"
            : $"/me/events/{eventId}";

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var graphEvent = await response.Content.ReadFromJsonAsync<GraphEvent>(JsonOptions, ct);
        return MapToCalendarEvent(graphEvent!);
    }

    public async Task<CalendarEvent> CreateEventAsync(string accessToken, EventCreateRequest createRequest,
        CancellationToken ct = default)
    {
        var path = createRequest.CalendarId is not null
            ? $"/me/calendars/{createRequest.CalendarId}/events"
            : "/me/events";

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(BuildCreateBody(createRequest), options: JsonOptions);

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var graphEvent = await response.Content.ReadFromJsonAsync<GraphEvent>(JsonOptions, ct);
        return MapToCalendarEvent(graphEvent!);
    }

    public async Task<CalendarEvent> UpdateEventAsync(string accessToken, string eventId,
        EventUpdateRequest updateRequest, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/me/events/{eventId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(BuildUpdateBody(updateRequest), options: JsonOptions);

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var graphEvent = await response.Content.ReadFromJsonAsync<GraphEvent>(JsonOptions, ct);
        return MapToCalendarEvent(graphEvent!);
    }

    public async Task DeleteEventAsync(string accessToken, string eventId, string? calendarId,
        CancellationToken ct = default)
    {
        var path = calendarId is not null
            ? $"/me/calendars/{calendarId}/events/{eventId}"
            : $"/me/events/{eventId}";

        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<FreeBusySlot>> CheckAvailabilityAsync(string accessToken,
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/me/calendar/getSchedule");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new
        {
            schedules = new[] { "me" },
            startTime = new GraphDateTimeTimeZone
            {
                DateTime = start.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                TimeZone = "UTC"
            },
            endTime = new GraphDateTimeTimeZone
            {
                DateTime = end.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                TimeZone = "UTC"
            }
        }, options: JsonOptions);

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var graphResponse =
            await response.Content.ReadFromJsonAsync<GraphListResponse<GraphScheduleResponse>>(JsonOptions, ct);
        return graphResponse!.Value
            .SelectMany(s => s.ScheduleItems)
            .Select(MapToFreeBusySlot)
            .ToList();
    }

    private static CalendarInfo MapToCalendarInfo(GraphCalendar c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Color = c.Color,
        IsDefault = c.IsDefaultCalendar,
        CanEdit = c.CanEdit
    };

    private static CalendarEvent MapToCalendarEvent(GraphEvent e) => new()
    {
        Id = e.Id,
        Subject = e.Subject,
        Body = e.Body?.Content,
        Start = ParseGraphDateTime(e.Start),
        End = ParseGraphDateTime(e.End),
        Location = e.Location?.DisplayName,
        IsAllDay = e.IsAllDay,
        Attendees = e.Attendees?.Select(a => a.EmailAddress.Address).ToList() ?? [],
        Organizer = e.Organizer?.EmailAddress.Address
    };

    private static FreeBusySlot MapToFreeBusySlot(GraphScheduleItem item) => new()
    {
        Start = ParseGraphDateTime(item.Start),
        End = ParseGraphDateTime(item.End),
        Status = ParseFreeBusyStatus(item.Status)
    };

    private static DateTimeOffset ParseGraphDateTime(GraphDateTimeTimeZone dt)
    {
        var parsed = System.DateTime.Parse(dt.DateTime, CultureInfo.InvariantCulture, DateTimeStyles.None);
        return new DateTimeOffset(parsed, TimeSpan.Zero);
    }

    private static FreeBusyStatus ParseFreeBusyStatus(string status) => status.ToLowerInvariant() switch
    {
        "busy" => FreeBusyStatus.Busy,
        "tentative" => FreeBusyStatus.Tentative,
        "oof" => FreeBusyStatus.OutOfOffice,
        _ => FreeBusyStatus.Free
    };

    private static object BuildCreateBody(EventCreateRequest r)
    {
        return new
        {
            subject = r.Subject,
            body = r.Body is not null ? new { content = r.Body, contentType = "text" } : null,
            start = new GraphDateTimeTimeZone
            {
                DateTime = r.Start.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                TimeZone = "UTC"
            },
            end = new GraphDateTimeTimeZone
            {
                DateTime = r.End.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                TimeZone = "UTC"
            },
            location = r.Location is not null ? new { displayName = r.Location } : null,
            isAllDay = r.IsAllDay,
            attendees = r.Attendees?.Select(a => new
            {
                emailAddress = new { address = a },
                type = "required"
            }).ToArray()
        };
    }

    private static object BuildUpdateBody(EventUpdateRequest r)
    {
        var body = new Dictionary<string, object>();

        if (r.Subject is not null) body["subject"] = r.Subject;
        if (r.Body is not null) body["body"] = new { content = r.Body, contentType = "text" };
        if (r.Start is not null)
            body["start"] = new GraphDateTimeTimeZone
            {
                DateTime = r.Start.Value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                TimeZone = "UTC"
            };
        if (r.End is not null)
            body["end"] = new GraphDateTimeTimeZone
            {
                DateTime = r.End.Value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                TimeZone = "UTC"
            };
        if (r.Location is not null) body["location"] = new { displayName = r.Location };
        if (r.IsAllDay is not null) body["isAllDay"] = r.IsAllDay.Value;
        if (r.Attendees is not null)
            body["attendees"] = r.Attendees.Select(a => new
            {
                emailAddress = new { address = a },
                type = "required"
            }).ToArray();

        return body;
    }
}

internal record GraphListResponse<T>
{
    [JsonPropertyName("value")]
    public List<T> Value { get; init; } = [];
}

internal record GraphCalendar
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("color")]
    public string? Color { get; init; }

    [JsonPropertyName("isDefaultCalendar")]
    public bool IsDefaultCalendar { get; init; }

    [JsonPropertyName("canEdit")]
    public bool CanEdit { get; init; }
}

internal record GraphEvent
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("subject")]
    public string Subject { get; init; } = "";

    [JsonPropertyName("body")]
    public GraphBody? Body { get; init; }

    [JsonPropertyName("start")]
    public GraphDateTimeTimeZone Start { get; init; } = new();

    [JsonPropertyName("end")]
    public GraphDateTimeTimeZone End { get; init; } = new();

    [JsonPropertyName("location")]
    public GraphLocation? Location { get; init; }

    [JsonPropertyName("isAllDay")]
    public bool IsAllDay { get; init; }

    [JsonPropertyName("attendees")]
    public List<GraphAttendee>? Attendees { get; init; }

    [JsonPropertyName("organizer")]
    public GraphOrganizer? Organizer { get; init; }
}

internal record GraphBody
{
    [JsonPropertyName("content")]
    public string Content { get; init; } = "";

    [JsonPropertyName("contentType")]
    public string ContentType { get; init; } = "";
}

internal record GraphDateTimeTimeZone
{
    [JsonPropertyName("dateTime")]
    public string DateTime { get; init; } = "";

    [JsonPropertyName("timeZone")]
    public string TimeZone { get; init; } = "";
}

internal record GraphLocation
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = "";
}

internal record GraphAttendee
{
    [JsonPropertyName("emailAddress")]
    public GraphEmailAddress EmailAddress { get; init; } = new();
}

internal record GraphOrganizer
{
    [JsonPropertyName("emailAddress")]
    public GraphEmailAddress EmailAddress { get; init; } = new();
}

internal record GraphEmailAddress
{
    [JsonPropertyName("address")]
    public string Address { get; init; } = "";
}

internal record GraphScheduleResponse
{
    [JsonPropertyName("scheduleItems")]
    public List<GraphScheduleItem> ScheduleItems { get; init; } = [];
}

internal record GraphScheduleItem
{
    [JsonPropertyName("start")]
    public GraphDateTimeTimeZone Start { get; init; } = new();

    [JsonPropertyName("end")]
    public GraphDateTimeTimeZone End { get; init; } = new();

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";
}
