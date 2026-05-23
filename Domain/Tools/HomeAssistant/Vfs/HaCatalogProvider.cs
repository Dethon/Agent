using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

// Cached source of truth for both the VFS engine and the slim index prompt. Caches the catalog
// for `CacheTtl`; on any HA failure returns HaCatalog.Empty with a short negative TTL so a
// transient outage doesn't blind the agent for the full window. Func<IHomeAssistantClient> (not a
// direct injection) keeps the transient, IHttpClientFactory-managed client from being pinned for
// the singleton's lifetime — same rationale as HomeAssistantSetupSummary.
public sealed class HaCatalogProvider(Func<IHomeAssistantClient> clientFactory, TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(30);

    // Single template render returns one JSON object covering every area and its entities —
    // the REST API has no other path into the area registry.
    private const string AreaTemplate =
        """{"areas":[{% for aid in areas() %}{% if not loop.first %},{% endif %}{"id":{{aid|tojson}},"name":{{area_name(aid)|tojson}},"entities":{{area_entities(aid)|list|tojson}}}{% endfor %}]}""";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HaCatalog _cached = HaCatalog.Empty;
    private DateTimeOffset _expiry = DateTimeOffset.MinValue;

    public async Task<HaCatalog> GetAsync(CancellationToken ct)
    {
        if (_time.GetUtcNow() < _expiry)
        {
            return _cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_time.GetUtcNow() < _expiry)
            {
                return _cached;
            }

            _cached = await TryBuildAsync(ct);
            _expiry = _time.GetUtcNow() + (_cached.Entities.Count == 0 ? FailureCacheTtl : CacheTtl);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HaCatalog> TryBuildAsync(CancellationToken ct)
    {
        try
        {
            var client = clientFactory();
            var states = client.ListStatesAsync(ct);
            var services = client.ListServicesAsync(ct);
            var areas = LoadAreasAsync(client, ct);
            await Task.WhenAll(states, services, areas);
            return new HaCatalog(states.Result, services.Result, areas.Result);
        }
        catch
        {
            return HaCatalog.Empty;
        }
    }

    private static async Task<IReadOnlyList<HaAreaEntities>> LoadAreasAsync(IHomeAssistantClient client, CancellationToken ct)
    {
        var rendered = await client.RenderTemplateAsync(AreaTemplate, ct);
        if (string.IsNullOrWhiteSpace(rendered))
        {
            return [];
        }
        try
        {
            var payload = JsonSerializer.Deserialize<AreaPayload>(rendered);
            return payload?.Areas?
                .Select(a => new HaAreaEntities(a.Id, a.Name, a.Entities ?? []))
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record AreaPayload
    {
        [JsonPropertyName("areas")] public IReadOnlyList<AreaDto>? Areas { get; init; }
    }

    private sealed record AreaDto
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("entities")] public IReadOnlyList<string>? Entities { get; init; }
    }
}