using System.Text.Json.Nodes;
using Domain.Agents;
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

public class WaitForDownloadTool(IDownloadClient client) : BaseTool, ITool
{
    public string Name => "WaitForDownload";

    public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var typedParams = ParseParams<WaitForDownloadParams>(parameters);

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
                    ["message"] = DownloadSystemPrompt.AfterDownloadPrompt(typedParams.DownloadId),
                    ["downloadId"] = typedParams.DownloadId
                };
            }

            await Task.Delay(1000, cancellationToken);
        }
    }

    public ToolDefinition GetToolDefinition()
    {
        return new ToolDefinition<WaitForDownloadParams>
        {
            Name = Name,
            Description = "Monitors a download until it ends and sends a notification with instructions when it does"
        };
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

            await Task.Delay(1000, cancellationToken);
        }

        return null;
    }
}