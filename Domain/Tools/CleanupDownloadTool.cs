using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;

namespace Domain.Tools;

public class CleanupDownloadTool(
    IDownloadClient downloadClient,
    IStateManager stateManager,
    IFileSystemClient fileSystemClient,
    DownloadPathConfig downloadPath)
{
    protected const string Name = "CleanupDownload";

    protected const string Description = """
                                         Removes a download task from the download manager and deletes any leftover files in the download directory.
                                         It can also be used to cancel a download if the user requests it.
                                         This tool first cleans up the download task, then removes the download directory.
                                         """;

    protected async Task<JsonNode> Run(string sessionId, int downloadId, CancellationToken ct)
    {
        // First cleanup the download task
        stateManager.TrackedDownloads.Remove(sessionId, downloadId);
        await downloadClient.Cleanup(downloadId, ct);

        // Then cleanup the download directory
        var path = $"{downloadPath.BaseDownloadPath}/{downloadId}";
        await fileSystemClient.RemoveDirectory(path, ct);

        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "Download task and directory removed successfully",
            ["downloadId"] = downloadId
        };
    }
}