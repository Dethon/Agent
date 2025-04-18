using System.Text.Json.Nodes;
using Domain.Tools;

namespace Infrastructure.ToolAdapters.FileDownloadTools;

public class QBittorrentDownloadAdapter : FileDownloadTool
{
    protected override async Task<JsonNode> Resolve(FileDownloadParams parameters, CancellationToken cancellationToken)
    {
        await Task.Delay(1000, cancellationToken);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "File download completed successfully"
        };
    }
}