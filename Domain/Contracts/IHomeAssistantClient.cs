using System.Text.Json.Nodes;
using JetBrains.Annotations;

namespace Domain.Contracts;

public interface IHomeAssistantClient
{
    Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default);
    Task<HaEntityState?> GetStateAsync(string entityId, CancellationToken ct = default);
    Task<IReadOnlyList<HaServiceDefinition>> ListServicesAsync(CancellationToken ct = default);
    Task<HaServiceCallResult> CallServiceAsync(
        string domain,
        string service,
        string? entityId,
        IReadOnlyDictionary<string, JsonNode?>? data,
        CancellationToken ct = default);
}

[PublicAPI]
public record HaEntityState
{
    public required string EntityId { get; init; }
    public required string State { get; init; }
    public IReadOnlyDictionary<string, JsonNode?> Attributes { get; init; } =
        new Dictionary<string, JsonNode?>();
    public DateTimeOffset? LastChanged { get; init; }
    public DateTimeOffset? LastUpdated { get; init; }
}

[PublicAPI]
public record HaServiceDefinition
{
    public required string Domain { get; init; }
    public required string Service { get; init; }
    public string? Description { get; init; }
    public IReadOnlyDictionary<string, HaServiceField> Fields { get; init; } =
        new Dictionary<string, HaServiceField>();
}

[PublicAPI]
public record HaServiceField
{
    public string? Description { get; init; }
    public bool Required { get; init; }
    public JsonNode? Example { get; init; }
}

[PublicAPI]
public record HaServiceCallResult
{
    public required IReadOnlyList<HaEntityState> ChangedEntities { get; init; }
}
