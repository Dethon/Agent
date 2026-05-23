using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaStateRenderer
{
    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true };

    public static string ToJson(HaEntityState entity)
    {
        var attributes = new JsonObject(
            entity.Attributes
                .OrderBy(a => a.Key, StringComparer.Ordinal)
                .Select(a => new KeyValuePair<string, JsonNode?>(a.Key, a.Value?.DeepClone())));

        var root = new JsonObject
        {
            ["entity_id"] = entity.EntityId,
            ["state"] = entity.State
        };
        if (entity.LastChanged is { } changed)
        {
            root["last_changed"] = changed.ToString("O");
        }
        if (entity.LastUpdated is { } updated)
        {
            root["last_updated"] = updated.ToString("O");
        }
        root["attributes"] = attributes;

        return root.ToJsonString(_indented);
    }
}