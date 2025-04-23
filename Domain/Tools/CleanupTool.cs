using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using JetBrains.Annotations;

namespace Domain.Tools;

[UsedImplicitly]
public record CleanupParams
{
    public required int DownloadId { get; [UsedImplicitly] init; }
}

public class CleanupTool(
    IDownloadClient downloadClient,
    IFileSystemClient fileSystemClient,
    string baseDownloadLocation) : BaseTool, ITool
{
    public string Name => "Cleanup";

    public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var typedParams = ParseParams<CleanupParams>(parameters);
        var downloadPath = $"{baseDownloadLocation}/{typedParams.DownloadId}";

        await fileSystemClient.RemoveDirectory(downloadPath, cancellationToken);
        await downloadClient.Cleanup($"{typedParams.DownloadId}", cancellationToken);

        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "Download leftovers removed",
            ["downloadId"] = typedParams.DownloadId
        };
    }

    public ToolDefinition GetToolDefinition()
    {
        return new ToolDefinition<CleanupParams>
        {
            Name = Name,
            Description = "Cleans a download leftover files after it has been organized."
        };
    }
}