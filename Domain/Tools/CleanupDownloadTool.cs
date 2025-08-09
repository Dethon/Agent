using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Domain.Tools;

[McpServerToolType]
public class CleanupDownloadTool(IDownloadClient downloadClient, IStateManager stateManager) : BaseTool
{
    private const string Name = "CleanupDownloadTask";

    private const string Description = """
                                       Removes a download task from the download manager.
                                       It can also be use to cancel a download if the user requests it.
                                       """;

    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        int downloadId,
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = context.Server.SessionId ?? "";
            stateManager.TrackedDownloads.Remove(sessionId, downloadId);
            await downloadClient.Cleanup(downloadId, cancellationToken);
            return CreateResponse(new JsonObject
            {
                ["status"] = "success",
                ["message"] = "Download task removed successfully",
                ["downloadId"] = downloadId
            });
        }
        catch (Exception ex)
        {
            return CreateResponse(ex);
        }
    }
}