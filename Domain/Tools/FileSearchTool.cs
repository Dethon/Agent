using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Attachments;
using JetBrains.Annotations;

namespace Domain.Tools;

[UsedImplicitly]
public record FileSearchParams
{
    public required string SearchString { get; [UsedImplicitly] init; }
}

public class FileSearchTool(
    ISearchClient client,
    SearchHistory history) : BaseTool<FileSearchParams>
{
    public override string Name => "FileSearch";

    public override string Description => """
                                          Search for a file in the internet using a search string. Search strings must be concise and
                                          not include too many details.
                                          """;

    public override async Task<ToolMessage> Run(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var typedParams = ParseParams(toolCall.Parameters);

        var results = await client.Search(typedParams.SearchString, cancellationToken);
        history.Add(results);
        return toolCall.ToToolMessage(new JsonObject
        {
            ["status"] = "success",
            ["message"] = "File search completed successfully",
            ["totalResults"] = results.Length,
            ["results"] = JsonSerializer.SerializeToNode(results)
        });
    }
}