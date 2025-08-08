using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Domain.Tools;

[McpServerToolType]
public class CleanupDownloadTool(IDownloadClient downloadClient) : BaseTool
{
    private const string Name = "CleanupDownload";

    private const string Description = """
                                       Removes a download task from the download manager.
                                       It can also be use to cancel a download if the user requests it.
                                       """;

    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(int downloadId, CancellationToken cancellationToken)
    {
        try
        {
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