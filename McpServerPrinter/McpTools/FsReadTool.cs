using System.ComponentModel;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsReadTool(PrinterQueueFileSystem fs)
{
    [McpServerTool(Name = "fs_read")]
    [Description("Read a queued document's text, or read status.json for the queue state.")]
    public async Task<CallToolResult> McpRun(string path, int? offset = null, int? limit = null, CancellationToken ct = default)
        => ToolResponse.Create(await fs.ReadAsync(path, offset, limit, ct));
}