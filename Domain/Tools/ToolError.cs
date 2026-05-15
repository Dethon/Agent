using System.Text.Json.Nodes;

namespace Domain.Tools;

// Standard error envelope for tool responses: { ok:false, errorCode, message, retryable, hint? }.
//
// When to call ToolError.Create explicitly:
//   - The tool has a specific code worth surfacing (e.g. captcha_required, element_not_found).
//   - The tool can offer concrete recovery guidance via `hint`.
//   - The retryable flag isn't obvious from the exception type.
//
// When to just throw and let the boundary wrap it:
//   - Generic argument/not-found failures with no extra context.
//   - Infrastructure.Utils.ToolResponse.Create(Exception) auto-wraps thrown exceptions
//     into this envelope at the MCP boundary using ToolResponse.MapErrorCode.
//
// Two paths exist on purpose: explicit envelopes for tools that know more than the
// generic mapping can express, fall-through throws for everything else.
public static class ToolError
{
    // Keep ToolResponse.MapErrorCode (Infrastructure/Utils/ToolResponse.cs) in sync —
    // every code the boundary wrapper produces must appear here.
    public static class Codes
    {
        public const string InvalidArgument = "invalid_argument";
        public const string NotFound = "not_found";
        public const string AlreadyExists = "already_exists";
        public const string Unavailable = "unavailable";
        public const string UnsupportedOperation = "unsupported_operation";
        public const string SessionNotFound = "session_not_found";
        public const string ElementNotFound = "element_not_found";
        public const string CaptchaRequired = "captcha_required";
        public const string Timeout = "timeout";
        public const string InternalError = "internal_error";
    }

    public static JsonObject Create(
        string errorCode,
        string message,
        bool retryable = false,
        string? hint = null)
    {
        var obj = new JsonObject
        {
            ["ok"] = false,
            ["errorCode"] = errorCode,
            ["message"] = message,
            ["retryable"] = retryable
        };

        if (!string.IsNullOrWhiteSpace(hint))
        {
            obj["hint"] = hint;
        }

        return obj;
    }
}