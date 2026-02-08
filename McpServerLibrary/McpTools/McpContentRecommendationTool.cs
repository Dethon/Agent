using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Tools;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class McpContentRecommendationTool : ContentRecommendationTool
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        RequestContext<CallToolRequestParams> context,
        [Description(
            "The user's content request including type, genre, mood, or criteria. Examples: 'sci-fi movies like Interstellar', 'upbeat pop songs for running', 'mystery novels set in Japan'")]
        string query,
        CancellationToken ct)
    {
        var server = context.Server;
        var parameters = CreateRequestSamplingParams(query);
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

    private static CreateMessageRequestParams CreateRequestSamplingParams(string userPrompt)
    {
        return new CreateMessageRequestParams
        {
            Messages =
            [
                new SamplingMessage
                {
                    Role = Role.User,
                    Content = [new TextContentBlock { Text = userPrompt }]
                }
            ],
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
