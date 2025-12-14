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
        var errors = new List<string>();

        // First cleanup the download task
        try
        {
            stateManager.TrackedDownloads.Remove(sessionId, downloadId);
            await downloadClient.Cleanup(downloadId, ct);
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to cleanup download task: {ex.Message}");
        }

        // Then cleanup the download directory (even if the first operation failed)
        try
        {
            var path = $"{downloadPath.BaseDownloadPath}/{downloadId}";
            await fileSystemClient.RemoveDirectory(path, ct);
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to remove download directory: {ex.Message}");
        }

        if (errors.Count > 0)
        {
            return new JsonObject
            {
                ["status"] = "partial_success",
                ["message"] = "Cleanup completed with errors",
                ["errors"] = new JsonArray(errors.Select(e => JsonValue.Create(e)).ToArray()),
                ["downloadId"] = downloadId
            };
        }

        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "Download task and directory removed successfully",
            ["downloadId"] = downloadId
        };
    }
}