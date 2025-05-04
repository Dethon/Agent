using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Attachments;
using JetBrains.Annotations;

namespace Domain.Tools;

[UsedImplicitly]
public record FileDownloadParams
{
    public required int SearchResultId { get; [UsedImplicitly] init; }
}

public class FileDownloadTool(
    IDownloadClient client,
    SearchHistory history,
    string baseDownloadLocation) : BaseTool, ITool
{
    public string Name => "FileDownload";

    public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var typedParams = ParseParams<FileDownloadParams>(parameters);

        var savePath = $"{baseDownloadLocation}/{typedParams.SearchResultId}";
        var itemToDownload = history.History[typedParams.SearchResultId];
        await client.Download(
            itemToDownload.Link,
            savePath,
            typedParams.SearchResultId,
            cancellationToken);

        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "Torrent added to qBittorrent successfully",
            ["downloadId"] = typedParams.SearchResultId
        };
    }

    public ToolDefinition GetToolDefinition()
    {
        return new ToolDefinition<FileDownloadParams>
        {
            Name = Name,
            Description = """
                          Download a file from the internet using a file id that can be obtained from the FileSearch 
                          tool. The SearchResultId parameter is the id EXACTLY as it appears in the response of the
                          FileSearch tool
                          """
        };
    }
}