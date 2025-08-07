using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Domain.Tools;

[McpServerToolType]
public class GetDownloadStatusTool(IDownloadClient client, IStateManager stateManager)
{
    private const string Name = "GetDownloadStatus";

    private const string Description = """
                                       Returns the status of download referenced by DownloadId.
                                       Progress is a percentage from 0 to 1, with 1 meaning 100%.
                                       DownSpeed and UpSpeed are in megabytes per second.
                                       Size is the total size of the download in megabytes.
                                       Eta is the estimated time until the completion of the download on minutes.
                                       """;

    [McpServerTool(Name = Name), Description(Description)]
    public async Task<string> Run(
        RequestContext<CallToolRequestParams> context, 
        int downloadId, 
        CancellationToken cancellationToken)
    {
        var sessionId = context.Server.SessionId ?? "";
        var downloadItem = await client.GetDownloadItem(downloadId, 3, 500, cancellationToken);
        if (downloadItem == null)
        {
            return "The download is missing, it probably got removed externally";
        }

        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = JsonSerializer.Serialize(downloadItem with
            {
                Title = stateManager.GetSearchResult(sessionId, downloadItem.Id)?.Title ?? "Missing Title",
            })
        }.ToJsonString();
    }
}