using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools;

public class CleanupDownloadTool(IDownloadClient downloadClient, IStateManager stateManager)
{
    protected const string Name = "CleanupDownloadTask";

    protected const string Description = """
                                         Removes a download task from the download manager.
                                         It can also be use to cancel a download if the user requests it.
                                         """;

    public async Task<JsonNode> Run(string sessionId, int downloadId, CancellationToken ct)
    {
        stateManager.TrackedDownloads.Remove(sessionId, downloadId);
        await downloadClient.Cleanup(downloadId, ct);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "Download task removed successfully",
            ["downloadId"] = downloadId
        };
    }
}