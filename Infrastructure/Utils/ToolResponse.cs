using System.Text.Json.Nodes;
using Domain.Exceptions;
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

    // Inspects the envelope so an `ok:false` payload propagates to MCP's IsError flag.
    // Previously this method always set IsError=false; the change lets envelope-shaped
    // failures from Domain tools surface at the MCP protocol level (and through any
    // downstream consumer that branches on IsError) without a separate signal channel.
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

    public static CallToolResult Create(JsonNode envelope, params string?[] bodies)
    {
        var isError = envelope is JsonObject obj
                      && obj.TryGetPropertyValue("ok", out var ok)
                      && ok is JsonValue v
                      && v.TryGetValue<bool>(out var okValue)
                      && !okValue;

        var content = new List<ContentBlock> { new TextContentBlock { Text = envelope.ToJsonString() } };
        content.AddRange(bodies
            .Where(b => b is not null)
            .Select(b => (ContentBlock)new TextContentBlock { Text = b! }));

        return new CallToolResult
        {
            IsError = isError,
            Content = content
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

    // Exception → envelope code mapping. FileNotFoundException/DirectoryNotFoundException
    // derive from IOException, so list them first; the switch matches the most specific arm.
    private static string MapErrorCode(Exception ex) => ex switch
    {
        ArgumentException => ToolError.Codes.InvalidArgument,
        HomeAssistantNotFoundException => ToolError.Codes.NotFound,
        FileNotFoundException => ToolError.Codes.NotFound,
        DirectoryNotFoundException => ToolError.Codes.NotFound,
        IOException => ToolError.Codes.AlreadyExists,
        HomeAssistantUnauthorizedException => ToolError.Codes.InvalidArgument,
        UnauthorizedAccessException => ToolError.Codes.InvalidArgument,
        TimeoutException => ToolError.Codes.Timeout,
        OperationCanceledException => ToolError.Codes.Timeout,
        _ => ToolError.Codes.InternalError
    };

    private static bool IsRetryable(Exception ex) => ex switch
    {
        ArgumentException => false,
        HomeAssistantNotFoundException => false,
        FileNotFoundException => false,
        DirectoryNotFoundException => false,
        IOException => false,
        HomeAssistantUnauthorizedException => false,
        UnauthorizedAccessException => false,
        TimeoutException => true,
        OperationCanceledException => true,
        _ => true
    };
}
