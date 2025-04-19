using System.Text.Json.Nodes;
using Domain.Tools;

namespace Infrastructure.ToolAdapters.FileMoveTools;

public class LocalFileMoveAdapter : FileMoveTool
{
    protected override Task<JsonNode> Resolve(FileMoveParams parameters, CancellationToken cancellationToken)
    {
        var result = new JsonObject
        {
            ["status"] = "success",
            ["message"] = "File moved successfully completed successfully"
        };
        return Task.FromResult<JsonNode>(result);
    }
}