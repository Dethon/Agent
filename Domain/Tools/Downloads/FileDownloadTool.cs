using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;

namespace Domain.Tools.Downloads;

public class FileDownloadTool(
    IDownloadClient client,
    ISearchResultsManager searchResultsManager,
    ITrackedDownloadsManager trackedDownloadsManager,
    DownloadPathConfig pathConfig)
{
    protected const string Name = "download_file";

    protected const string Description = """
                                         Download a file from the internet using a file id that can be obtained from the
                                         file_search tool.
                                         The SearchResultId parameter is the id EXACTLY as it appears in the response of
                                         the file_search tool.
                                         """;

    protected async Task<JsonNode> Run(string sessionId, int searchResultId, CancellationToken ct)
    {
        var existing = await client.GetDownloadItem(searchResultId, ct);
        if (existing is not null)
        {
            return ToolError.Create(
                ToolError.Codes.AlreadyExists,
                "Download with this id already exists, try another id",
                retryable: false);
        }

        var savePath = $"{pathConfig.BaseDownloadPath}/{searchResultId}";
        var itemToDownload = searchResultsManager.Get(sessionId, searchResultId);
        if (itemToDownload == null)
        {
            return ToolError.Create(
                ToolError.Codes.NotFound,
                $"No search result found for id {searchResultId}. " +
                "Make sure to run the file_search tool first and use the correct id.",
                retryable: false);
        }

        await client.Download(itemToDownload.Link, savePath, searchResultId, ct);

        trackedDownloadsManager.Add(sessionId, searchResultId);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = $"""
                           Download with id {searchResultId} started successfully.
                           User will notify yoy when it is completed."
                           """
        };
    }
}