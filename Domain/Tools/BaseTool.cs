using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Domain.Tools;

public class BaseTool
{
    protected static CallToolResult CreateResponse(Exception ex)
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

    protected static CallToolResult CreateResponse(JsonObject json)
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

    protected static CallToolResult CreateResponse(string message)
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