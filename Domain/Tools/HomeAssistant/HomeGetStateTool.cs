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

        Use this BEFORE calling a service when you need an attribute value as
        input — e.g. a media_player's `source_list` before `select_source`,
        a climate entity's `preset_modes` before `set_preset_mode`, or any
        value that only exists on the entity. (For area/room targets, the
        area `id` from the appended "Areas" snapshot is usually the answer
        — no entity lookup needed.)

        DO NOT call this AFTER `home_call_service` returned `ok:true`. That
        result is authoritative — the service ran. HA propagates state
        updates asynchronously, so a read right after the call usually
        returns the pre-action value and tells you nothing new. Re-reading
        to "confirm" is wasted work and misleading.

        Returns ok:false / errorCode:not_found if the entity does not exist.
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
