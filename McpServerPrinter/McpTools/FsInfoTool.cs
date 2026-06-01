using System.ComponentModel;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsInfoTool(PrinterQueueFileSystem fs)
{
    [McpServerTool(Name = "fs_info")]
    [Description("Get metadata for a queued document or the queue root.")]
    public async Task<CallToolResult> McpRun(string path, CancellationToken ct = default)
        => ToolResponse.Create(await fs.InfoAsync(path, ct));
}