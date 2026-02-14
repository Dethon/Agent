namespace Infrastructure.Calendar;

public record CalendarAuthSettings
{
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required string TenantId { get; init; }
}
