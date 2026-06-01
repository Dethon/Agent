using System.ComponentModel;
using Domain.DTOs;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsEditTool(PrinterQueueFileSystem fs)
{
    [McpServerTool(Name = "fs_edit")]
    [Description("Edit a queued text document; cancels the old job and re-queues the new version.")]
    public async Task<CallToolResult> McpRun(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct = default)
        => ToolResponse.Create(await fs.EditAsync(path, edits, ct));
}