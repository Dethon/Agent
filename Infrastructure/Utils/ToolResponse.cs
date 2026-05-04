using System.Text.Json.Nodes;
using Domain.Tools;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Utils;

public static class ToolResponse
{
    public static CallToolResult Create(Exception ex)
    {
        var envelope = ToolError.Create(
            MapErrorCode(ex),
            ex.Message,
            retryable: IsRetryable(ex));

        return new CallToolResult
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = envelope.ToJsonString()
                }
            ]
        };
    }

    public static CallToolResult Create(JsonNode json)
    {
        var isError = json is JsonObject obj
                      && obj.TryGetPropertyValue("ok", out var ok)
                      && ok is JsonValue v
                      && v.TryGetValue<bool>(out var okValue)
                      && !okValue;

        return new CallToolResult
        {
            IsError = isError,
            Content =
            [
                new TextContentBlock
                {
                    Text = json.ToJsonString()
                }
            ]
        };
    }

    public static CallToolResult Create(string message)
    {
        return new CallToolResult
        {
            IsError = false,
            Content =
            [
                new TextContentBlock
                {
                    Text = message
                }
            ]
        };
    }

    private static string MapErrorCode(Exception ex) => ex switch
    {
        ArgumentException => ToolError.Codes.InvalidArgument,
        FileNotFoundException => ToolError.Codes.NotFound,
        DirectoryNotFoundException => ToolError.Codes.NotFound,
        UnauthorizedAccessException => ToolError.Codes.InvalidArgument,
        TimeoutException => ToolError.Codes.Timeout,
        OperationCanceledException => ToolError.Codes.Timeout,
        _ => ToolError.Codes.InternalError
    };

    private static bool IsRetryable(Exception ex) => ex switch
    {
        ArgumentException => false,
        FileNotFoundException => false,
        DirectoryNotFoundException => false,
        UnauthorizedAccessException => false,
        TimeoutException => true,
        OperationCanceledException => true,
        _ => true
    };
}
