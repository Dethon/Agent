using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using ModelContextProtocol.Server;

namespace Domain.Tools;

[McpServerToolType]
public class CleanupDownloadTool(IDownloadClient downloadClient)
{
    private const string Name = "CleanupDownload";

    private const string Description = """
                                       Removes a download task from the download manager.
                                       It can also be use to cancel a download if the user requests it.
                                       """;

    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<string> Run(int downloadId, CancellationToken cancellationToken)
    {
        await downloadClient.Cleanup(downloadId, cancellationToken);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "Download task removed successfully",
            ["downloadId"] = downloadId
        }.ToJsonString();
    }
}