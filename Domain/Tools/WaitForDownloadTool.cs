using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Attachments;
using JetBrains.Annotations;

namespace Domain.Tools;

[UsedImplicitly]
public record WaitForDownloadParams
{
    public required int DownloadId { get; [UsedImplicitly] init; }
}

public class WaitForDownloadTool(DownloadMonitor monitor) : BaseTool, ITool
{
    public string Name => "WaitForDownload";

    public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var typedParams = ParseParams<WaitForDownloadParams>(parameters);
        while (!cancellationToken.IsCancellationRequested && 
               !await monitor.PopCompletedDownload(typedParams.DownloadId, cancellationToken))
        {
            await Task.Delay(1000, cancellationToken);
        }

        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = DownloadSystemPrompt.AfterDownloadPrompt(typedParams.DownloadId),
            ["downloadId"] = typedParams.DownloadId
        };
    }

    public ToolDefinition GetToolDefinition()
    {
        return new ToolDefinition<WaitForDownloadParams>
        {
            Name = Name,
            Description = "Monitors a download until it ends and sends a notification with instructions when it does"
        };
    }
}