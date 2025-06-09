using System.Text.Json.Nodes;
using Domain.Contracts;
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
    string baseDownloadLocation) : BaseTool<FileDownloadTool, FileDownloadParams>, IToolWithMetadata
{
    public static string Name => "FileDownload";

    public static string Description => """
                                        Download a file from the internet using a file id that can be obtained from the FileSearch 
                                        tool. The SearchResultId parameter is the id EXACTLY as it appears in the response of the
                                        FileSearch tool
                                        """;

    public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var typedParams = ParseParams(parameters);

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
}