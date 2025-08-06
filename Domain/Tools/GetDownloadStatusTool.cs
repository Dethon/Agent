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
                                        Returns the status of download referenced by DownloadId.
                                        Progress is a percentage from 0 to 1, with 1 meaning 100%.
                                        DownSpeed and UpSpeed are in megabytes per second.
                                        Size is the total size of the download in megabytes.
                                        Eta is the estimated time until the completion of the download on minutes.
                                        """;

    public override async Task<ToolMessage> Run(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var typedParams = ParseParams(toolCall.Parameters);
        
        cancellationToken.ThrowIfCancellationRequested();
        var downloadItem = await client.GetDownloadItem(typedParams.DownloadId, 3, 500, cancellationToken);
        if (downloadItem == null)
        {
            throw new MissingDownloadException("The download is missing, it probably got removed externally");
        }

        return toolCall.ToToolMessage(new JsonObject
        {
            ["status"] = "success",
            ["message"] = JsonSerializer.Serialize(downloadItem with
            {
                Title = history.History[downloadItem.Id].Title
            })
        });
    }
}