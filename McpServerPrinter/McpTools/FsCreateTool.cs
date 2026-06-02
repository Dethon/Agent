using System.ComponentModel;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsCreateTool(PrinterQueueFileSystem fs)
{
    [McpServerTool(Name = "fs_create")]
    [Description("Queue a new text document for printing at /print-queue/<filename>.")]
    public async Task<CallToolResult> McpRun(string path, string content, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default)
        => ToolResponse.Create(await fs.CreateAsync(path, content, overwrite, createDirectories, ct));
}