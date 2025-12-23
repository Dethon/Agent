using System.Text.Json;
using System.Text.Json.Nodes;
using JetBrains.Annotations;

namespace Domain.Extensions;

public static class JsonExtensions
{
    extension(JsonElement element)
    {
        [PublicAPI]
        public bool TryGetProperty(string propertyName, StringComparison comparisonType, out JsonElement jsonElement)
        {
            ArgumentNullException.ThrowIfNull(propertyName);

            if (element.ValueKind != JsonValueKind.Object)
            {
                jsonElement = default;
                return false;
            }

            var result = element
                .EnumerateObject()
                .Where(o => propertyName.Equals(o.Name, comparisonType))
                .ToArray();
            if (result.Length == 0)
            {
                jsonElement = default;
                return false;
            }

            jsonElement = result[0].Value;
            return true;
        }

        public JsonElement GetProperty(string propertyName, StringComparison comparisonType)
        {
            var success = element.TryGetProperty(propertyName, comparisonType, out var result);
            return success
                ? result
                : throw new KeyNotFoundException($"Property '{propertyName}' not found in JsonElement.");
        }

        public JsonNode? ToJsonNode()
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.Array => JsonArray.Create(element),
                JsonValueKind.Object => JsonObject.Create(element),
                _ => JsonValue.Create(element)
            };
        }
    }
}