using System.Text.Json.Nodes;
using Domain.Contracts;
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
    string baseDownloadLocation) : BaseTool<CleanupTool, CleanupParams>, IToolWithMetadata
{
    public static Type? ParamsType => typeof(CleanupParams);
    public static string Name => "Cleanup";

    public static string Description => """
                                        Removes a everything that is left over in a download directory.
                                        It can also be use to cancel a download if the user requests it.
                                        """;

    public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var typedParams = ParseParams(parameters);
        var downloadPath = $"{baseDownloadLocation}/{typedParams.DownloadId}";

        await fileSystemClient.RemoveDirectory(downloadPath, cancellationToken);
        await downloadClient.Cleanup(typedParams.DownloadId, cancellationToken);

        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "Download leftovers removed successfully",
            ["downloadId"] = typedParams.DownloadId
        };
    }
}