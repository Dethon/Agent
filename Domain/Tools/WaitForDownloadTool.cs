using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Exceptions;
using JetBrains.Annotations;

namespace Domain.Tools;

[UsedImplicitly]
public record WaitForDownloadParams
{
    public required int DownloadId { get; [UsedImplicitly] init; }
}

public class WaitForDownloadTool(
    IDownloadClient client) : BaseTool<WaitForDownloadTool, WaitForDownloadParams>, IToolWithMetadata
{
    public static string Name => "WaitForDownload";

    public static string Description => """
                                        Monitors a download until it ends and sends a notification with instructions 
                                        when it does.
                                        The DownloadId parameter is the id EXACTLY as it appears in the response of the
                                        FileDownload tool.
                                        """;

    public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var typedParams = ParseParams(parameters);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var downloadItem = await GetDownloadItem(typedParams.DownloadId, cancellationToken);
            if (downloadItem == null)
            {
                throw new MissingDownloadException("The download is missing, it probably got removed externally");
            }

            if (downloadItem.Status == DownloadStatus.Completed)
            {
                return new JsonObject
                {
                    ["status"] = "success",
                    ["message"] = $"""
                                   The download with id {typedParams.DownloadId} just finished. Now your task is to 
                                   organize the files that were downloaded by download {typedParams.DownloadId} into the 
                                   current library structure. 
                                   If there is no appropriate folder for the category you should create it. 
                                   To explore the library structure you must first know all directories and then the 
                                   files that are already present in the relevant directories (both source and 
                                   destination).
                                   Afterwards, if and only if the organization succeeded, clean up the download 
                                   leftovers.
                                   Hint: Use the ListDirectories, ListFiles, Move and Cleanup tools.
                                   """,
                    ["downloadId"] = typedParams.DownloadId
                };
            }

            await Task.Delay(1000, cancellationToken);
        }
    }

    private async Task<DownloadItem?> GetDownloadItem(int downloadId, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var downloadItem = await client.GetDownloadItem(downloadId, cancellationToken);
            if (downloadItem != null)
            {
                return downloadItem;
            }

            await Task.Delay(500, cancellationToken);
        }

        return null;
    }
}