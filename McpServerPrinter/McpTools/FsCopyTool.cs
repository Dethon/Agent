using System.ComponentModel;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsCopyTool(PrinterQueueFileSystem fs)
{
    [McpServerTool(Name = "fs_copy")]
    [Description("Duplicate a queued document under a new name (queues another print job).")]
    public async Task<CallToolResult> McpRun(string sourcePath, string destinationPath, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default)
        => ToolResponse.Create(await fs.CopyAsync(sourcePath, destinationPath, overwrite, createDirectories, ct));
}