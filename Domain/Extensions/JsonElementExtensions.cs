using System.Text.Json;
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
    }
}