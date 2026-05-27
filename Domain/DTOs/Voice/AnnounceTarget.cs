namespace Domain.DTOs.Voice;

public record AnnounceTarget
{
    public string? SatelliteId { get; init; }
    public string? Room { get; init; }
    public bool? All { get; init; }
}