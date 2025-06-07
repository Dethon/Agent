using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Infrastructure.Utils;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Infrastructure.MCP;

public class McpTool<T> : McpServerTool where T : IToolWithMetadata
{
    public override Tool ProtocolTool { get; } = new()
    {
        Name = T.Name,
        Description = T.Description,
        InputSchema = ConvertToJsonElement(JsonSchema.CreateParametersSchema(T.ParamsType))
    };

    public override async ValueTask<CallToolResponse> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var jsonNode = request.Params?.Arguments != null
            ? JsonNode.Parse(JsonSerializer.Serialize(request.Params.Arguments))
            : null;
        var result = await request.Services!.GetRequiredService<T>().Run(jsonNode, cancellationToken);
        return new CallToolResponse
        {
            IsError = false,
            Content =
            [
                new Content
                {
                    Type = "text",
                    Text = result.ToJsonString()
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