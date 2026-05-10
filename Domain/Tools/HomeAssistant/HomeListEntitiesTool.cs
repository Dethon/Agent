using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant;

public class HomeListEntitiesTool(IHomeAssistantClient client)
{
    protected const string Name = "home_list_entities";

    protected const string Description =
        """
        Lists entities from Home Assistant. Filter by `domain` (e.g. 'vacuum', 'light')
        and/or `area` (substring match against friendly_name). Returns a trimmed
        projection: entity_id, state, friendly_name, domain, last_changed.
        """;

    protected async Task<JsonObject> RunAsync(string? domain, string? area, int? limit, CancellationToken ct)
    {
        var states = await client.ListStatesAsync(ct);
        var effectiveLimit = limit is > 0 ? limit.Value : 100;

        var filtered = states
            .Where(e => domain is null || EntityDomain(e.EntityId).Equals(domain, StringComparison.OrdinalIgnoreCase))
            .Where(e => area is null || FriendlyName(e).Contains(area, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.EntityId, StringComparer.OrdinalIgnoreCase)
            .Take(effectiveLimit)
            .Select(e =>
            {
                var obj = new JsonObject
                {
                    ["entity_id"] = e.EntityId,
                    ["state"] = e.State,
                    ["domain"] = EntityDomain(e.EntityId),
                    ["friendly_name"] = FriendlyName(e)
                };
                if (e.LastChanged is { } changed)
                {
                    obj["last_changed"] = changed.ToString("O");
                }
                return obj;
            })
            .ToArray<JsonNode?>();

        return new JsonObject
        {
            ["ok"] = true,
            ["entities"] = new JsonArray(filtered)
        };
    }

    private static string EntityDomain(string entityId)
    {
        var dot = entityId.IndexOf('.');
        return dot < 0 ? entityId : entityId[..dot];
    }

    private static string FriendlyName(HaEntityState entity)
        => entity.Attributes.TryGetValue("friendly_name", out var fn) && fn is JsonValue v
           && v.TryGetValue<string>(out var s) && s is not null
            ? s
            : entity.EntityId;
}
