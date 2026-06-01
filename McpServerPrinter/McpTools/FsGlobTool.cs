using System.ComponentModel;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsGlobTool(PrinterQueueFileSystem fs)
{
    [McpServerTool(Name = "fs_glob")]
    [Description("List queued documents (plus status.json) matching a glob pattern.")]
    public async Task<CallToolResult> McpRun(string basePath, string pattern, CancellationToken ct = default)
        => ToolResponse.Create(await fs.GlobAsync(basePath, pattern, ct));
}