using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Exceptions;
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

    public override async Task<ToolMessage> Run(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var typedParams = ParseParams(toolCall.Parameters);
        await CheckDownloadNotAdded(typedParams.SearchResultId, cancellationToken);
        
        var savePath = $"{baseDownloadLocation}/{typedParams.SearchResultId}";
        var itemToDownload = history.History[typedParams.SearchResultId];
        await client.Download(
            itemToDownload.Link,
            savePath,
            typedParams.SearchResultId,
            cancellationToken);

        var toolMessage = toolCall.ToToolMessage(new JsonObject
        {
            ["status"] = "success",
            ["message"] = "Torrent added to qBittorrent successfully. You will be notified by user when it finishes",
            ["downloadPath"] = savePath,
            ["downloadId"] = typedParams.SearchResultId
        });

        return toolMessage with
        {
            LongRunningTask = GetNotification(typedParams.SearchResultId, cancellationToken)
        };
    }

    private async Task CheckDownloadNotAdded(int downloadId, CancellationToken cancellationToken)
    {
        var downloadItem = await client.GetDownloadItem(downloadId, 3, 350, cancellationToken);
        if (downloadItem != null)
        {
            throw new InvalidOperationException("Download with this id already exists, try another id");
        }
    }
    
    private async Task<Message?> GetNotification(int downloadId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var downloadItem = await client.GetDownloadItem(downloadId, 3, 500, cancellationToken);
            if (downloadItem == null)
            {
                throw new MissingDownloadException("The download is missing, it probably got removed externally");
            }

            if (downloadItem.Status == DownloadStatus.Completed)
            {
                return new Message
                {
                    Role = Role.User,
                    Content = $"""
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
                               """
                };
            }
            await Task.Delay(1000, cancellationToken);
        }
    }
}