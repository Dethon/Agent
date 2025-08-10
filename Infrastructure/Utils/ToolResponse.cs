using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Utils;

public static class ToolResponse
{
    public static CallToolResult Create(Exception ex)
    {
        return new CallToolResult
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = ex.Message
                }
            ]
        };
    }

    public static CallToolResult Create(JsonNode json)
    {
        return new CallToolResult
        {
            IsError = false,
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
}