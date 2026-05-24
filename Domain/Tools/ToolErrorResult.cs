using System.Text.Json.Nodes;

namespace Domain.Tools;

// Typed counterpart to ToolError.Create's JsonObject envelope: { ok:false, errorCode, message, retryable, hint? }.
// Single home for the error-envelope shape and the ok:false discriminator.
public sealed record ToolErrorResult
{
    public required string ErrorCode { get; init; }
    public required string Message { get; init; }
    public required bool Retryable { get; init; }
    public string? Hint { get; init; }

    public JsonObject ToNode()
    {
        var obj = new JsonObject
        {
            ["ok"] = false,
            ["errorCode"] = ErrorCode,
            ["message"] = Message,
            ["retryable"] = Retryable
        };

        if (!string.IsNullOrWhiteSpace(Hint))
        {
            obj["hint"] = Hint;
        }

        return obj;
    }

    public static bool IsErrorEnvelope(JsonNode? json)
        => json is JsonObject obj
           && obj.TryGetPropertyValue("ok", out var ok)
           && ok is JsonValue v
           && v.TryGetValue<bool>(out var okValue)
           && !okValue;

    public static ToolErrorResult? FromEnvelope(JsonNode? json)
    {
        if (json is not JsonObject obj || !IsErrorEnvelope(obj))
        {
            return null;
        }

        // && r means "value parsed AND is true"; a present false (or absent/non-bool) yields false — intended.
        var retryable = obj["retryable"] is JsonValue rv && rv.TryGetValue<bool>(out var r) && r;
        return new ToolErrorResult
        {
            ErrorCode = (obj["errorCode"] as JsonValue)?.GetValue<string>() ?? ToolError.Codes.InternalError,
            Message = (obj["message"] as JsonValue)?.GetValue<string>() ?? string.Empty,
            Retryable = retryable,
            Hint = (obj["hint"] as JsonValue)?.GetValue<string>()
        };
    }
}