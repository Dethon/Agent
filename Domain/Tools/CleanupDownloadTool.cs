using System.IO;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;
using FluentResults;

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
        var cleanupTaskResult = await CleanupDownloadTask(sessionId, downloadId, ct);
        var cleanupDirectoryResult = await CleanupDownloadDirectory(downloadId, ct);
        var fullResult = Result.Merge(cleanupTaskResult, cleanupDirectoryResult);

        if (fullResult.IsSuccess)
        {
            return new JsonObject
            {
                ["status"] = "success",
                ["message"] = "Download task and directory removed successfully",
                ["downloadId"] = downloadId
            };
        }
        
        var allErrors = fullResult.Errors.Select(e => e.Message).ToArray();
        var jsonErrors = new JsonArray(allErrors.Select(e => JsonValue.Create(e)).ToArray());
        if (cleanupTaskResult.IsSuccess || cleanupDirectoryResult.IsSuccess)
        {
            return new JsonObject
            {
                ["status"] = "partial_success",
                ["message"] = "Cleanup partially completed",
                ["downloadId"] = downloadId,
                ["taskCleanedUp"] = cleanupTaskResult.IsSuccess,
                ["directoryCleanedUp"] = cleanupDirectoryResult.IsSuccess,
                ["errors"] = jsonErrors
            };
        }

        return new JsonObject
        {
            ["status"] = "failure",
            ["message"] = "Cleanup failed completely",
            ["downloadId"] = downloadId,
            ["errors"] = jsonErrors
        };
    }
    
    private async Task<Result> CleanupDownloadTask(string sessionId, int downloadId, CancellationToken ct)
    {
        try
        {
            stateManager.TrackedDownloads.Remove(sessionId, downloadId);
            await downloadClient.Cleanup(downloadId, ct);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail($"Failed to cleanup download task: {ex.Message}");
        }
    }
    
    private async Task<Result> CleanupDownloadDirectory(int downloadId, CancellationToken ct)
    {
        try
        {
            var basePath = downloadPath.BaseDownloadPath;
            var path = basePath.Contains('/')
                ? $"{basePath.TrimEnd('/', '\\')}/{downloadId}"
                : Path.Combine(basePath, downloadId.ToString());
            await fileSystemClient.RemoveDirectory(path, ct);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail($"Failed to remove download directory: {ex.Message}");
        }
    }
}