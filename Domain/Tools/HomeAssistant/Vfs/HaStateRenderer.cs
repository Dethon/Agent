using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaStateRenderer
{
    public static string ToYaml(HaEntityState entity)
    {
        var sb = new StringBuilder();
        sb.Append("entity_id: ").Append(entity.EntityId).Append('\n');
        sb.Append("state: ").Append(JsonValue.Create(entity.State).ToJsonString()).Append('\n');
        if (entity.LastChanged is { } changed)
        {
            sb.Append("last_changed: ").Append(changed.ToString("O")).Append('\n');
        }
        if (entity.LastUpdated is { } updated)
        {
            sb.Append("last_updated: ").Append(updated.ToString("O")).Append('\n');
        }

        if (entity.Attributes.Count == 0)
        {
            sb.Append("attributes: {}");
            return sb.ToString();
        }

        sb.Append("attributes:");
        foreach (var (key, value) in entity.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            sb.Append("\n  ").Append(key).Append(": ").Append(value?.ToJsonString() ?? "null");
        }
        return sb.ToString();
    }
}