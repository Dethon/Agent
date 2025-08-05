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
        var toolCall = new ToolCall
        {
            Name = "",
            Id = "",
            Parameters = request.Params?.Arguments != null
                ? JsonNode.Parse(JsonSerializer.Serialize(request.Params.Arguments))
                : null
        };

        var result = await request.Services!.GetRequiredService<T>().Run(toolCall, cancellationToken);
        return new CallToolResult
        {
            IsError = false,
            Content =
            [
                new TextContentBlock
                {
                    Text = result.Content
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