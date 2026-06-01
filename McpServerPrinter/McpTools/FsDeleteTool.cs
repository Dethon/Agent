using System.ComponentModel;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsDeleteTool(PrinterQueueFileSystem fs)
{
    [McpServerTool(Name = "fs_delete")]
    [Description("Remove a queued document. Cancels it if it has not finished printing.")]
    public async Task<CallToolResult> McpRun(string path, CancellationToken ct = default)
        => ToolResponse.Create(await fs.DeleteAsync(path, ct));
}