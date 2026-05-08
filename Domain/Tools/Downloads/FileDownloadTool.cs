using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
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
                                         Download a file from the internet.

                                         Provide ONE of:
                                           - searchResultId: an id from a prior file_search call.
                                           - link + title: a magnet URI or .torrent URL obtained from any other tool, plus a
                                             descriptive title (e.g. the release name with quality and group, taken from
                                             wherever the link was found).

                                         Do not provide both. The link path is intended as a fallback when file_search returns
                                         no usable results.
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

        var itemToDownload = searchResultsManager.Get(sessionId, searchResultId);
        if (itemToDownload == null)
        {
            return ToolError.Create(
                ToolError.Codes.NotFound,
                $"No search result found for id {searchResultId}. " +
                "Make sure to run the file_search tool first and use the correct id.",
                retryable: false);
        }

        return await StartDownload(sessionId, searchResultId, itemToDownload.Link, ct);
    }

    protected async Task<JsonNode> Run(string sessionId, string link, string title, CancellationToken ct)
    {
        var id = link.GetHashCode();

        var existing = await client.GetDownloadItem(id, ct);
        if (existing is not null)
        {
            return ToolError.Create(
                ToolError.Codes.AlreadyExists,
                "Download with this link already exists, choose a different link",
                retryable: false);
        }

        var synthetic = new SearchResult
        {
            Id = id,
            Title = title,
            Link = link
        };
        searchResultsManager.Add(sessionId, [synthetic]);

        return await StartDownload(sessionId, id, link, ct);
    }

    private async Task<JsonNode> StartDownload(string sessionId, int id, string link, CancellationToken ct)
    {
        var savePath = $"{pathConfig.BaseDownloadPath}/{id}";
        await client.Download(link, savePath, id, ct);

        trackedDownloadsManager.Add(sessionId, id);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = $"""
                           Download with id {id} started successfully.
                           User will notify you when it is completed."
                           """
        };
    }
}
