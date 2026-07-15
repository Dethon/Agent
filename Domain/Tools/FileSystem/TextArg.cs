using System.Text.Json;
using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

// Lenient coercion for arguments that are declared as text but may arrive as structured
// JSON when the model misuses a tool (e.g. passing a JSON object as a *.json file's
// content). Keeps text_create / text_edit resilient without weakening their advertised
// "string" schema: parameter types are unchanged; only the binding is made tolerant.
internal static class TextArg
{
    public static string Coerce(object? raw) => raw switch
    {
        JsonElement element => CoerceElement(element),
        string text => text,
        null => string.Empty,
        _ => raw.ToString() ?? string.Empty
    };

    public static bool WasCoerced(object? raw) => raw switch
    {
        JsonElement element => element.ValueKind is not JsonValueKind.String,
        string => false,
        null => false,
        _ => true
    };

    public static bool WasCoercedArg(AIFunctionArguments? arguments, string key) =>
        arguments is not null && arguments.TryGetValue(key, out var raw) && WasCoerced(raw);

    public static IReadOnlyList<TextEdit> CoerceEdits(object? raw)
    {
        if (raw is not JsonElement array || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Select(element => new TextEdit(
                Coerce(element.TryGetProperty("oldString", out var oldString) ? oldString : default),
                Coerce(element.TryGetProperty("newString", out var newString) ? newString : default),
                element.TryGetProperty("replaceAll", out var replaceAll) && replaceAll.ValueKind == JsonValueKind.True))
            .ToList();
    }

    public static bool EditsWereCoercedArg(AIFunctionArguments? arguments)
    {
        if (arguments is null || !arguments.TryGetValue("edits", out var raw)
            || raw is not JsonElement array || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return array.EnumerateArray()
            .Any(element => IsStructuredProperty(element, "oldString") || IsStructuredProperty(element, "newString"));
    }

    private static bool IsStructuredProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.String;

    private static string CoerceElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => element.GetRawText()
    };
}