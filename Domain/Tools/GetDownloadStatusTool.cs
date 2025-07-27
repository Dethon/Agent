using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Exceptions;
using Domain.Tools.Attachments;
using JetBrains.Annotations;

namespace Domain.Tools;

[UsedImplicitly]
public record GetDownloadStatusParams
{
    public required int DownloadId { get; [UsedImplicitly] init; }
}

public class GetDownloadStatusTool(IDownloadClient client, SearchHistory history) :
    BaseTool<GetDownloadStatusTool, GetDownloadStatusParams>, IToolWithMetadata
{
    public static string Name => "GetDownloadStatus";

    public static string Description => """
                                        Returns the status of download process that is currently happening.
                                        Progress is a percentage from 0 to 100.
                                        DownSpeed and UpSpeed are in megabytes per second.
                                        """;

    public override async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
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

            return new JsonObject
            {
                ["status"] = "success",
                ["message"] = JsonSerializer.Serialize(downloadItem with
                {
                    Title = history.History[downloadItem.Id].Title
                })
            };
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