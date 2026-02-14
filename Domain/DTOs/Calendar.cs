namespace Domain.DTOs;

public record CalendarInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Color { get; init; }
    public bool IsDefault { get; init; }
    public bool CanEdit { get; init; }
}

public record CalendarEvent
{
    public required string Id { get; init; }
    public string? CalendarId { get; init; }
    public required string Subject { get; init; }
    public string? Body { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public string? Location { get; init; }
    public bool IsAllDay { get; init; }
    public string? Recurrence { get; init; }
    public IReadOnlyList<string> Attendees { get; init; } = [];
    public string? Organizer { get; init; }
    public string? Status { get; init; }
}

public record EventCreateRequest
{
    public string? CalendarId { get; init; }
    public required string Subject { get; init; }
    public string? Body { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public string? Location { get; init; }
    public bool? IsAllDay { get; init; }
    public IReadOnlyList<string>? Attendees { get; init; }
    public string? Recurrence { get; init; }
}

public record EventUpdateRequest
{
    public string? Subject { get; init; }
    public string? Body { get; init; }
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public string? Location { get; init; }
    public bool? IsAllDay { get; init; }
    public IReadOnlyList<string>? Attendees { get; init; }
    public string? Recurrence { get; init; }
}

public record FreeBusySlot
{
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public required FreeBusyStatus Status { get; init; }
}

public enum FreeBusyStatus
{
    Free,
    Busy,
    Tentative,
    OutOfOffice
}

public record OAuthTokens
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
