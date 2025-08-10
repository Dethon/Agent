using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools;

public class GetDownloadStatusTool(IDownloadClient client, IStateManager stateManager)
{
    protected const string Name = "GetDownloadStatus";

    protected const string Description = """
                                         Returns the status of download referenced by DownloadId.
                                         Progress is a percentage from 0 to 1, with 1 meaning 100%.
                                         DownSpeed and UpSpeed are in megabytes per second.
                                         Size is the total size of the download in megabytes.
                                         Eta is the estimated time until the completion of the download on minutes.
                                         """;

    protected async Task<JsonNode> Run(string sessionId, int downloadId, CancellationToken ct)
    {
        var downloadItem = await client.GetDownloadItem(downloadId, ct);
        if (downloadItem == null)
        {
            return new JsonObject
            {
                ["status"] = "mising",
                ["message"] = "The download is missing, it probably got removed externally"
            };
        }

        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = JsonSerializer.Serialize(new DownloadStatus(downloadItem)
            {
                Title = stateManager.SearchResults.Get(sessionId, downloadItem.Id)?.Title ?? "Missing Title"
            })
        };
    }
}