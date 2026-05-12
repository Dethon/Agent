using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant;

public class HomeCallServiceTool(IHomeAssistantClient client)
{
    protected const string Name = "home_call_service";

    protected const string Description =
        """
        Calls a Home Assistant service. Pass `domain` and `service` (e.g. 'vacuum'/'start').
        Use the `entity_id` parameter for the target entity; service-specific options go in
        `data` as a JSON object.

        Returns `{ok:true, changed_entities, response?}` on success. `ok:true`
        is the authoritative signal that the service ran — do not follow up
        with `home_get_state` to confirm; HA's async state propagation makes
        that read stale and pointless. `changed_entities` is best-effort and
        may be empty even when the action succeeded — empty is not failure.
        `response` carries query-style payloads (forecasts, calendar
        events, position getters) when the service returns data; absent
        otherwise.
        """;

    protected async Task<JsonObject> RunAsync(
        string domain, string service, string? entityId, JsonObject? data, CancellationToken ct)
    {
        IReadOnlyDictionary<string, JsonNode?>? payload = data is null
            ? null
            : data
                .Where(kvp => entityId is null || !kvp.Key.Equals("entity_id", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.DeepClone());

        var result = await client.CallServiceAsync(domain, service, entityId, payload, ct);

        var changed = result.ChangedEntities
            .Select(e => (JsonNode?)new JsonObject
            {
                ["entity_id"] = e.EntityId,
                ["state"] = e.State
            })
            .ToArray();

        var envelope = new JsonObject
        {
            ["ok"] = true,
            ["changed_entities"] = new JsonArray(changed)
        };
        if (result.Response is not null)
        {
            envelope["response"] = result.Response.DeepClone();
        }
        return envelope;
    }
}
