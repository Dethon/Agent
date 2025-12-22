using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Downloads;

public record ResubscribeDownloadsResult(JsonNode Response, bool HasNewSubscriptions);

public class ResubscribeDownloadsTool(
    IDownloadClient downloadClient,
    ITrackedDownloadsManager trackedDownloadsManager)
{
    protected const string Name = "ResubscribeDownloads";

    protected const string Description = """
                                         Resubscribes to download progress updates for the specified download IDs.
                                         Use this tool after an application restart to resume tracking downloads that were 
                                         previously started. The agent should know the download IDs from the conversation history.
                                         For downloads that are NotFound or AlreadyCompleted, check the downloads folder to see 
                                         if the files need to be organized.
                                         """;

    protected async Task<ResubscribeDownloadsResult> Run(string sessionId, int[] downloadIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(downloadIds);

        if (downloadIds.Length == 0)
        {
            var emptyResponse = new JsonObject
            {
                ["status"] = "error",
                ["message"] = "No download IDs provided"
            };
            return new ResubscribeDownloadsResult(emptyResponse, false);
        }

        var trackedIds = trackedDownloadsManager.Get(sessionId) ?? [];
        var results = new List<ResubscribeResult>();

        foreach (var downloadId in downloadIds)
        {
            var result = await ProcessDownloadId(sessionId, downloadId, trackedIds, ct);
            results.Add(result);
        }

        var hasNewSubscriptions = results.Any(r => r.Status == ResubscribeStatus.Resubscribed);
        return new ResubscribeDownloadsResult(BuildResponse(results), hasNewSubscriptions);
    }

    private async Task<ResubscribeResult> ProcessDownloadId(
        string sessionId,
        int downloadId,
        int[] trackedIds,
        CancellationToken ct)
    {
        if (trackedIds.Contains(downloadId))
        {
            return new ResubscribeResult(
                downloadId,
                ResubscribeStatus.AlreadyTracked,
                $"Download {downloadId} is already being tracked");
        }

        var downloadItem = await downloadClient.GetDownloadItem(downloadId, ct);

        if (downloadItem is null)
        {
            return new ResubscribeResult(
                downloadId,
                ResubscribeStatus.NotFound,
                $"Download {downloadId} not found - it may have been removed externally or completed. " +
                "Check the downloads folder to see if it needs to be organized.");
        }

        if (downloadItem.State is DownloadState.Completed)
        {
            return new ResubscribeResult(
                downloadId,
                ResubscribeStatus.AlreadyCompleted,
                $"Download {downloadId} has already completed. " +
                "Check the downloads folder to see if it needs to be organized.");
        }

        trackedDownloadsManager.Add(sessionId, downloadId);
        return new ResubscribeResult(
            downloadId,
            ResubscribeStatus.Resubscribed,
            $"Successfully resubscribed to download {downloadId}");
    }

    private static JsonObject BuildResponse(List<ResubscribeResult> results)
    {
        var resubscribed = results.Where(r => r.Status == ResubscribeStatus.Resubscribed).ToList();
        var needsAttention = results
            .Where(r => r.Status is ResubscribeStatus.NotFound or ResubscribeStatus.AlreadyCompleted)
            .ToList();
        var alreadyTracked = results.Where(r => r.Status == ResubscribeStatus.AlreadyTracked).ToList();

        var resultsArray = new JsonArray();
        foreach (var result in results)
        {
            resultsArray.Add(new JsonObject
            {
                ["downloadId"] = result.DownloadId,
                ["status"] = result.Status.ToString(),
                ["message"] = result.Message
            });
        }

        return new JsonObject
        {
            ["status"] = needsAttention.Count == results.Count ? "attention_required" : "success",
            ["summary"] = new JsonObject
            {
                ["resubscribed"] = resubscribed.Count,
                ["needsAttention"] = needsAttention.Count,
                ["alreadyTracked"] = alreadyTracked.Count
            },
            ["results"] = resultsArray
        };
    }
}