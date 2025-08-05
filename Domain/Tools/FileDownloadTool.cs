using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Caching.Memory;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Domain.Tools;

[McpServerToolType]
public class FileDownloadTool(IDownloadClient client, IMemoryCache cache, string baseDownloadLocation)
{
    private const string Name = "FileDownload";

    private const string Description = """
                                       Download a file from the internet using a file id that can be obtained from the 
                                       FileSearch tool. 
                                       The SearchResultId parameter is the id EXACTLY as it appears in the response of 
                                       the FileSearch tool.
                                       """;

    [McpServerTool(Name = Name), Description(Description)]
    public async Task<string> Run(
        IMcpServer server, 
        RequestContext<CallToolRequestParams> context, 
        int searchResultId, 
        CancellationToken cancellationToken)
    {
        await CheckDownloadNotAdded(searchResultId, cancellationToken);

        var savePath = $"{baseDownloadLocation}/{searchResultId}";
        var itemToDownload = cache.Get<SearchResult>(searchResultId);
        if (itemToDownload == null)
        {
            throw new InvalidOperationException($"No search result found for id {searchResultId}.");
        }

        await client.Download(
            itemToDownload.Link,
            savePath,
            searchResultId,
            cancellationToken);

        return await TrackProgress(server, context, searchResultId, cancellationToken);
    }

    private async Task CheckDownloadNotAdded(int downloadId, CancellationToken cancellationToken)
    {
        var downloadItem = await client.GetDownloadItem(downloadId, 3, 350, cancellationToken);
        if (downloadItem != null)
        {
            throw new InvalidOperationException("Download with this id already exists, try another id");
        }
    }

    private async Task<string> TrackProgress(
        IMcpServer server, 
        RequestContext<CallToolRequestParams> context, 
        int downloadId, 
        CancellationToken cancellationToken)
    {
        var progressToken = context.Params?.ProgressToken;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var downloadItem = await client.GetDownloadItem(downloadId, 3, 500, cancellationToken);
            
            if (downloadItem == null)
            {
                return $"The download with id {downloadId} is missing, it probably got removed externally.";
            }
            
            if (progressToken is not null)
            {
                await server.SendNotificationAsync("notifications/progress", new
                {
                    Progress = downloadItem.Progress * 100, 
                    Total = 100,
                    progressToken
                }, cancellationToken: cancellationToken);
            }     

            if (downloadItem.Status == DownloadStatus.Completed)
            {
                return $"""
                        The download with id {downloadId} just finished. Now your task is to 
                        organize the files that were downloaded by download {downloadId} into the 
                        current library structure. 
                        If there is no appropriate folder for the category you should create it. 
                        To explore the library structure you must first know all directories and then the 
                        files that are already present in the relevant directories (both source and 
                        destination).
                        Afterwards, if and only if the organization succeeded, clean up the download 
                        leftovers.
                        Hint: Use the ListDirectories, ListFiles, Move and Cleanup tools.
                        """;
            }

            await Task.Delay(1000, cancellationToken);
        }
    }
}