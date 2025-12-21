using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;

namespace Domain.Tools.Downloads;

public class FileDownloadTool(IDownloadClient client, IStateManager stateManager, DownloadPathConfig pathConfig)
{
    protected const string Name = "FileDownload";

    protected const string Description = """
                                         Download a file from the internet using a file id that can be obtained from the 
                                         FileSearch tool. 
                                         The SearchResultId parameter is the id EXACTLY as it appears in the response of 
                                         the FileSearch tool.
                                         """;

    protected async Task<JsonNode> Run(string sessionId, int searchResultId, CancellationToken ct)
    {
        await CheckDownloadNotAdded(searchResultId, ct);

        var savePath = $"{pathConfig.BaseDownloadPath}/{searchResultId}";
        var itemToDownload = stateManager.SearchResults.Get(sessionId, searchResultId);
        if (itemToDownload == null)
        {
            throw new InvalidOperationException(
                $"No search result found for id {searchResultId}. " +
                "Make sure to run the FileSearch tool first and use the correct id.");
        }

        await client.Download(itemToDownload.Link, savePath, searchResultId, ct);

        stateManager.TrackedDownloads.Add(sessionId, searchResultId);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = $"""
                           Download with id {searchResultId} started successfully. 
                           User will notify yoy when it is completed."
                           """
        };
    }

    private async Task CheckDownloadNotAdded(int downloadId, CancellationToken cancellationToken)
    {
        var downloadItem = await client.GetDownloadItem(downloadId, cancellationToken);
        if (downloadItem != null)
        {
            throw new InvalidOperationException("Download with this id already exists, try another id");
        }
    }
}