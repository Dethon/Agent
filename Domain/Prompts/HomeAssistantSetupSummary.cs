using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Domain.Contracts;

namespace Domain.Prompts;

// Builds a Markdown "Current setup" block that gets appended to HomeAssistantPrompt at
// MCP-prompt-fetch time. The block surfaces the three things agents otherwise have to
// hunt for across multiple list_* calls:
//   1. Active integration service domains (the source of vendor-specific actions like
//      roborock.get_maps that don't live under the entity's class domain).
//   2. HA areas with the entities assigned to each.
//   3. Entities grouped by class domain (so the agent sees the whole device inventory
//      at a glance).
//
// We cache the rendered string for `_cacheTtl` so repeated MCP prompt fetches don't
// hammer HA. Anthropic's prompt cache also benefits from the result staying byte-
// stable across refreshes when nothing in HA has actually changed.
//
// Failure mode: if HA is unreachable while building, we return an empty string and
// the caller falls back to the static prompt alone. We never let a transient HA
// hiccup break the agent's session.
//
// Why a `Func<IHomeAssistantClient>` instead of a direct injection: this class is
// registered as a singleton (for caching) but `IHomeAssistantClient` is transient
// behind `IHttpClientFactory`. Holding the client directly would pin one transient
// instance — and its `HttpMessageHandler` — for the process lifetime, defeating
// `HandlerLifetime` rotation. The factory resolves a fresh client per build so the
// HTTP handler stays under `IHttpClientFactory`'s rotation policy.
public class HomeAssistantSetupSummary(
    Func<IHomeAssistantClient> clientFactory,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan _failureCacheTtl = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    // HA's standard "class" domains — everything an entity sits under, plus the
    // built-ins HA always registers. Service domains NOT in this set are integration-
    // owned and worth surfacing prominently.
    private static readonly HashSet<string> _classDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "alarm_control_panel", "automation", "binary_sensor", "button", "calendar",
        "camera", "climate", "cover", "date", "datetime", "device_tracker", "event",
        "fan", "group", "humidifier", "image", "input_boolean", "input_button",
        "input_datetime", "input_number", "input_select", "input_text", "lawn_mower",
        "light", "lock", "logbook", "media_player", "notify", "number",
        "persistent_notification", "person", "remote", "scene", "schedule", "script",
        "select", "sensor", "siren", "stt", "sun", "switch", "system_log", "text",
        "time", "timer", "todo", "tts", "update", "vacuum", "valve", "wake_word",
        "water_heater", "weather", "zone",
        // Core HA service holders that aren't entity classes but also aren't
        // integration-owned in a meaningful sense.
        "homeassistant", "frontend", "recorder", "logger", "hassio", "backup",
        "cloud", "conversation", "shopping_list", "media_source", "websocket_api"
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _cached = string.Empty;
    private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;

    public async Task<string> GetAsync(CancellationToken ct = default)
    {
        if (_timeProvider.GetUtcNow() < _cacheExpiry)
        {
            return _cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_timeProvider.GetUtcNow() < _cacheExpiry)
            {
                return _cached;
            }

            _cached = await TryBuildAsync(ct);
            // Empty result means BuildAsync threw — keep the negative cache short so a
            // transient HA outage doesn't blind the agent for the full 30-min window.
            _cacheExpiry = _timeProvider.GetUtcNow() + (string.IsNullOrEmpty(_cached) ? _failureCacheTtl : _cacheTtl);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> TryBuildAsync(CancellationToken ct)
    {
        try
        {
            return await BuildAsync(ct);
        }
        catch
        {
            return string.Empty;
        }
    }

    // Public for unit-testing — calls all three HA endpoints and renders the block.
    public async Task<string> BuildAsync(CancellationToken ct)
    {
        var client = clientFactory();
        var statesTask = client.ListStatesAsync(ct);
        var servicesTask = client.ListServicesAsync(ct);
        var areasTask = LoadAreasAsync(client, ct);
        await Task.WhenAll(statesTask, servicesTask, areasTask);

        return Render(statesTask.Result, servicesTask.Result, areasTask.Result);
    }

    private static async Task<IReadOnlyList<AreaInfo>> LoadAreasAsync(IHomeAssistantClient client, CancellationToken ct)
    {
        // Single template render returns one JSON object covering every area and its
        // assigned entities. The REST API has no other path into the area registry.
        const string template = """
            {"areas":[{% for aid in areas() %}{% if not loop.first %},{% endif %}{"id":{{aid|tojson}},"name":{{area_name(aid)|tojson}},"entities":{{area_entities(aid)|list|tojson}}}{% endfor %}]}
            """;

        var rendered = await client.RenderTemplateAsync(template, ct);
        if (string.IsNullOrWhiteSpace(rendered))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<AreaPayload>(rendered);
            return parsed?.Areas ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Render(
        IReadOnlyList<HaEntityState> states,
        IReadOnlyList<HaServiceDefinition> services,
        IReadOnlyList<AreaInfo> areas)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Current Home Assistant setup");
        sb.AppendLine();

        RenderIntegrations(sb, services);
        RenderAreas(sb, areas, states);
        RenderEntitiesByDomain(sb, states);

        return sb.ToString().TrimEnd();
    }

    private static void RenderIntegrations(StringBuilder sb, IReadOnlyList<HaServiceDefinition> services)
    {
        var integrationDomains = services
            .Select(s => s.Domain)
            .Where(d => !_classDomains.Contains(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        sb.AppendLine("### Integration service domains");
        sb.AppendLine(
            "Vendor- or integration-specific actions live under these domains (separate from the entity's class domain). Discover the surface with `home_list_services(domain=<name>)`.");
        if (integrationDomains.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("_None detected — only built-in HA domains are active._");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine(string.Join(", ", integrationDomains));
        }
        sb.AppendLine();
    }

    private static void RenderAreas(
        StringBuilder sb,
        IReadOnlyList<AreaInfo> areas,
        IReadOnlyList<HaEntityState> states)
    {
        sb.AppendLine("### Areas");
        if (areas.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("_No areas configured in HA — entities are unstructured by room._");
            sb.AppendLine();
            return;
        }

        var byId = states.ToDictionary(s => s.EntityId, StringComparer.OrdinalIgnoreCase);
        var assigned = areas.SelectMany(a => a.Entities ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

        sb.AppendLine();
        foreach (var area in areas.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            var entities = (area.Entities ?? [])
                .Where(byId.ContainsKey)
                .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .ToList();
            sb.Append("- **").Append(area.Name).Append("** (id: `").Append(area.Id).Append("`)");
            if (entities.Count == 0)
            {
                sb.AppendLine(" — _no entities assigned_");
            }
            else
            {
                sb.Append(" — ").Append(entities.Count).AppendLine(" entities");
                sb.Append("  - ").AppendLine(string.Join(", ", entities.Select(e => FormatEntity(byId[e]))));
            }
        }

        var unassigned = byId.Keys.Except(assigned, StringComparer.OrdinalIgnoreCase).ToList();
        if (unassigned.Count > 0)
        {
            sb.Append("- **(unassigned)** — ").Append(unassigned.Count).AppendLine(" entities not placed in any area");
        }
        sb.AppendLine();
    }

    private static void RenderEntitiesByDomain(StringBuilder sb, IReadOnlyList<HaEntityState> states)
    {
        var grouped = states
            .GroupBy(s => EntityDomain(s.EntityId), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        sb.AppendLine("### Entities by class domain");
        if (grouped.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("_No entities exposed._");
            return;
        }

        sb.AppendLine();
        foreach (var group in grouped)
        {
            var formatted = group
                .OrderBy(e => e.EntityId, StringComparer.OrdinalIgnoreCase)
                .Select(FormatEntity)
                .ToList();
            sb.Append("- **").Append(group.Key).Append("** (").Append(formatted.Count).Append("): ");
            sb.AppendLine(string.Join(", ", formatted));
        }
    }

    // entity_id (Friendly Name) when the friendly_name attribute exists and differs from the
    // entity_id; bare entity_id otherwise. Users assign HA friendly names because raw IDs
    // (`light.salon_lamp_2_zigbee_abc`) are hard to map back to the device in conversation.
    private static string FormatEntity(HaEntityState entity)
    {
        if (!entity.Attributes.TryGetValue("friendly_name", out var node) || node is not JsonValue v
            || !v.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name)
            || name.Equals(entity.EntityId, StringComparison.OrdinalIgnoreCase))
        {
            return entity.EntityId;
        }
        return $"{entity.EntityId} ({name})";
    }

    private static string EntityDomain(string entityId)
    {
        var dot = entityId.IndexOf('.');
        return dot < 0 ? entityId : entityId[..dot];
    }

    private sealed record AreaPayload
    {
        [JsonPropertyName("areas")] public IReadOnlyList<AreaInfo>? Areas { get; init; }
    }

    public sealed record AreaInfo
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("entities")] public IReadOnlyList<string>? Entities { get; init; }
    }
}
