using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Domain.Tools;

public record DownloadPathConfig(string BaseDownloadLocation);

[McpServerToolType]
public class FileDownloadTool(IDownloadClient client, IStateManager stateManager, DownloadPathConfig pathConfig)
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
        RequestContext<CallToolRequestParams> context, 
        int searchResultId, 
        CancellationToken cancellationToken)
    {
        var sessionId = context.Server.SessionId ?? "";
        await CheckDownloadNotAdded(searchResultId, cancellationToken);

        var savePath = $"{pathConfig.BaseDownloadLocation}/{searchResultId}";
        var itemToDownload = stateManager.GetSearchResult(sessionId, searchResultId);
        if (itemToDownload == null)
        {
            throw new InvalidOperationException($"No search result found for id {searchResultId}.");
        }

        await client.Download(
            itemToDownload.Link,
            savePath,
            searchResultId,
            cancellationToken);
        
        stateManager.TrackDownload(sessionId, searchResultId);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = $"""
                           Download with id {searchResultId} started successfully. 
                           User will notify yoy when it is completed."
                           """
        }.ToJsonString();
    }

    private async Task CheckDownloadNotAdded(int downloadId, CancellationToken cancellationToken)
    {
        var downloadItem = await client.GetDownloadItem(downloadId, 3, 350, cancellationToken);
        if (downloadItem != null)
        {
            throw new InvalidOperationException("Download with this id already exists, try another id");
        }
    }
}