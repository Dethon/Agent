using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaArgParser
{
    public static JsonObject Parse(IReadOnlyList<string> tokens, HaServiceDefinition svc)
    {
        var data = new JsonObject();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Expected a --flag but found '{token}'.");
            }

            var name = token[2..];
            if (!svc.Fields.TryGetValue(name, out var field))
            {
                throw new ArgumentException(
                    $"Unknown argument '--{name}'. Run `{svc.Service}.sh --help` for the field list.");
            }

            if (i + 1 >= tokens.Count)
            {
                throw new ArgumentException($"Missing value for '--{name}'.");
            }

            data[name] = Coerce(name, tokens[++i], field.Selector);
        }
        return data;
    }

    private static JsonNode? Coerce(string name, string raw, JsonNode? selector)
    {
        if (selector?["number"] is not null)
        {
            // JsonNode.Parse yields a JsonElement-backed value whose GetValue<int>()/GetValue<double>()
            // both work; JsonValue.Create(long/double) does NOT convert across numeric types on .NET 10.
            // double.TryParse accepts NaN/Infinity/".5" which JsonNode.Parse rejects, so guard both so a
            // malformed value surfaces as ArgumentException (exec -> exit 2), not an uncaught JsonException.
            if (!double.TryParse(raw, CultureInfo.InvariantCulture, out _))
            {
                throw new ArgumentException($"--{name} expects a number, got '{raw}'.");
            }
            try
            {
                return JsonNode.Parse(raw);
            }
            catch (JsonException)
            {
                throw new ArgumentException($"--{name} expects a number, got '{raw}'.");
            }
        }
        if (selector?["boolean"] is not null)
        {
            return bool.TryParse(raw, out var b)
                ? JsonValue.Create(b)
                : throw new ArgumentException($"--{name} expects true or false, got '{raw}'.");
        }
        if (IsMultiSelect(selector))
        {
            return new JsonArray(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        }
        if (selector?["object"] is not null)
        {
            try
            {
                return JsonNode.Parse(raw);
            }
            catch (JsonException)
            {
                throw new ArgumentException($"--{name} expects a JSON value, got '{raw}'.");
            }
        }
        return JsonValue.Create(raw);
    }

    private static bool IsMultiSelect(JsonNode? selector) =>
        selector?["select"]?["multiple"]?.GetValue<bool>() == true;
}