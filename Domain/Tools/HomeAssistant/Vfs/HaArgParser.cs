using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaArgParser
{
    // `commandName` is the action-file name without `.sh` (e.g. `music_assistant.play_media` for a
    // cross-domain service); it only feeds error hints so they point at the file the caller invoked.
    // Defaults to the bare service name for same-domain callers.
    public static JsonObject Parse(IReadOnlyList<string> tokens, HaServiceDefinition svc, string? commandName = null)
    {
        var command = commandName ?? svc.Service;
        var data = new JsonObject();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Expected a --flag but found '{token}'.");
            }

            // Accept both GNU long-option forms: `--flag value` and `--flag=value`. Split on the
            // first '=' only, so a value may itself contain '='.
            var body = token[2..];
            var eq = body.IndexOf('=');
            var name = eq < 0 ? body : body[..eq];
            if (!svc.Fields.TryGetValue(name, out var field))
            {
                throw new ArgumentException(
                    $"Unknown argument '--{name}'. Run `{command}.sh --help` for the field list.");
            }

            string raw;
            if (eq >= 0)
            {
                raw = body[(eq + 1)..];
            }
            else if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                raw = tokens[++i];
            }
            else
            {
                // The next token is another flag (or there is none): the space-form value is missing.
                // Don't silently swallow the following flag as this one's value.
                throw new ArgumentException(
                    $"Missing value for '--{name}'. Use --{name}=<value> if the value begins with '--'.");
            }

            data[name] = Coerce(name, raw, field.Selector);
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
        if (TryGetSelectOptions(selector, out var options) && !options.Contains(raw, StringComparer.Ordinal))
        {
            throw new ArgumentException($"--{name} expects one of [{string.Join(", ", options)}], got '{raw}'.");
        }
        return JsonValue.Create(raw);
    }

    private static bool IsMultiSelect(JsonNode? selector) =>
        selector?["select"]?["multiple"]?.GetValue<bool>() == true;

    // Single-select options (multi-select is coerced earlier). Options may be bare strings or
    // {value,label} objects, mirroring HaServiceHelpRenderer.TypeOf.
    private static bool TryGetSelectOptions(JsonNode? selector, out IReadOnlyList<string> options)
    {
        if (selector?["select"]?["options"] is JsonArray arr)
        {
            options = arr
                .Select(o => o is JsonObject obj ? obj["value"]?.ToString() : o?.ToString())
                .OfType<string>()
                .ToList();
            return options.Count > 0;
        }
        options = [];
        return false;
    }
}