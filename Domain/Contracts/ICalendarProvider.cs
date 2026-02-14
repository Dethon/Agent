using Domain.DTOs;

namespace Domain.Contracts;

public interface ICalendarProvider
{
    Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string accessToken, CancellationToken ct = default);
    Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(string accessToken, string? calendarId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default);
    Task<CalendarEvent> GetEventAsync(string accessToken, string eventId, string? calendarId, CancellationToken ct = default);
    Task<CalendarEvent> CreateEventAsync(string accessToken, EventCreateRequest request, CancellationToken ct = default);
    Task<CalendarEvent> UpdateEventAsync(string accessToken, string eventId, EventUpdateRequest request, CancellationToken ct = default);
    Task DeleteEventAsync(string accessToken, string eventId, string? calendarId, CancellationToken ct = default);
    Task<IReadOnlyList<FreeBusySlot>> CheckAvailabilityAsync(string accessToken, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default);
}
