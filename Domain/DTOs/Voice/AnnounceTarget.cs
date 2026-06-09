namespace Domain.DTOs.Voice;

public record AnnounceTarget
{
    public string? SatelliteId { get; init; }
    public IReadOnlyList<string>? SatelliteIds { get; init; }
    public string? Room { get; init; }
    public bool? All { get; init; }
}