using System.Text.Json.Nodes;

namespace Domain.Tools;

public static class ToolError
{
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
        public const string CrossFilesystem = "cross_filesystem";
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
