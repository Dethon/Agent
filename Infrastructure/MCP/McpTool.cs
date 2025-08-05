using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Utils;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Infrastructure.MCP;

// WIP
public class McpTool<T> : McpServerTool where T : IToolWithMetadata
{
    public override Tool ProtocolTool { get; } = new()
    {
        Name = T.Name,
        Description = T.Description,
        InputSchema = ConvertToJsonElement(JsonSchema.CreateParametersSchema(T.ParamsType))
    };

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        try
        {
            var toolCall = GetToolCall(request.Params?.Arguments);
            var result = await request.Services!.GetRequiredService<T>().Run(toolCall, cancellationToken);
            return GetToolResult(result.Content, false);
        }
        catch (Exception ex)
        {
            return GetToolResult(ex.Message, true);
        }
    }

    private static ToolCall GetToolCall(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        return new ToolCall
        {
            Name = "",
            Id = "",
            Parameters = arguments != null
                ? JsonNode.Parse(JsonSerializer.Serialize(arguments))
                : null
        };
    }

    private static CallToolResult GetToolResult(string message, bool isError)
    {
        return new CallToolResult
        {
            IsError = isError,
            Content =
            [
                new TextContentBlock
                {
                    Text = message
                }
            ]
        };
    }

    private static JsonElement ConvertToJsonElement(JsonNode? node)
    {
        return node == null
            ? JsonDocument.Parse("{}").RootElement
            : JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }
}