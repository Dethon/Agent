using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools;

namespace Domain.Tools.HomeAssistant;

public class HomeGetStateTool(IHomeAssistantClient client)
{
    protected const string Name = "home_get_state";

    protected const string Description =
        """
        Gets the current state and attributes of one Home Assistant entity by entity_id.
        Returns `{ok, entity_id, state, attributes, last_changed?, last_updated?}` or
        `{ok:false, errorCode:"not_found"}` when the entity does not exist.
        """;

    protected async Task<JsonObject> RunAsync(string entityId, CancellationToken ct)
    {
        var entity = await client.GetStateAsync(entityId, ct);
        if (entity is null)
        {
            return ToolError.Create(
                ToolError.Codes.NotFound,
                $"Home Assistant entity '{entityId}' not found.");
        }

        var attributes = new JsonObject();
        foreach (var (key, value) in entity.Attributes)
        {
            attributes[key] = value?.DeepClone();
        }

        var result = new JsonObject
        {
            ["ok"] = true,
            ["entity_id"] = entity.EntityId,
            ["state"] = entity.State,
            ["attributes"] = attributes
        };
        if (entity.LastChanged is { } changed)
        {
            result["last_changed"] = changed.ToString("O");
        }
        if (entity.LastUpdated is { } updated)
        {
            result["last_updated"] = updated.ToString("O");
        }
        return result;
    }
}
