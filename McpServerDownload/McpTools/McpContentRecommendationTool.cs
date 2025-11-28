using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Tools;
using Infrastructure.Agents.Mappers;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerDownload.McpTools;

[McpServerToolType]
public class McpContentRecommendationTool(ILogger<McpContentRecommendationTool> logger) : ContentRecommendationTool
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        RequestContext<CallToolRequestParams> context,
        string userPrompt,
        CancellationToken ct)
    {
        try
        {
            var server = context.Server;
            var parameters = CreateRequestSamplingParams(userPrompt);
            var result = await server.SampleAsync(parameters, ct);
            var contents = result.Content
                .Where(x => x is TextContentBlock)
                .Cast<TextContentBlock>()
                .Select(x => x.Text)
                .ToArray();

            if (contents.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Expected text response but received a different type {result.Content.GetType()}");
            }

            return ToolResponse.Create(string.Join("", contents));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in {ToolName} tool", Name);
            return ToolResponse.Create(ex);
        }
    }

    private static CreateMessageRequestParams CreateRequestSamplingParams(string userPrompt)
    {
        var messages = GetFullPrompt(userPrompt);
        return new CreateMessageRequestParams
        {
            Messages = messages.Select(x => x.ToSamplingMessage()).ToArray(),
            SystemPrompt = SystemPrompt,
            IncludeContext = ContextInclusion.None,
            MaxTokens = 5000,
            Metadata = JsonSerializer.Deserialize<JsonElement>(new JsonObject
            {
                ["tracker"] = Name
            }.ToJsonString())
        };
    }
}