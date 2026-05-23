using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaStateRenderer
{
    // Attribute keys that are safe as bare YAML plain scalars. HA keys are normally snake_case
    // identifiers; anything else (spaces, colons, …) is JSON-quoted so the document stays valid YAML.
    private static readonly Regex _safePlainKey = new("^[A-Za-z0-9_][A-Za-z0-9_.-]*$", RegexOptions.Compiled);

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
            sb.Append("\n  ").Append(YamlKey(key)).Append(": ").Append(value?.ToJsonString() ?? "null");
        }
        return sb.ToString();
    }

    // JSON strings are valid YAML flow scalars, so values are already YAML-safe via ToJsonString();
    // only bare keys can break the document — quote the unsafe ones the same way.
    private static string YamlKey(string key) =>
        _safePlainKey.IsMatch(key) ? key : JsonValue.Create(key)!.ToJsonString();
}