namespace Domain.DTOs.Voice;

// Domain-side view of a voice satellite: Domain cannot see the channel server's SatelliteConfig,
// and only needs enough to name a satellite back to the agent when a target fails to resolve.
public record SatelliteDescriptor(string Id, string Room);