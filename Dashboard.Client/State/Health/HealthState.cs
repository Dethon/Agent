namespace Dashboard.Client.State.Health;

public record ServiceHealth(string Service, bool IsHealthy, string? LastSeen);

public record HealthState
{
    public IReadOnlyList<ServiceHealth> Services { get; init; } = [];
}