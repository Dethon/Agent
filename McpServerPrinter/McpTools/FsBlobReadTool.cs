using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsBlobReadTool(IPrintSpool spool)
{
    [McpServerTool(Name = "fs_blob_read")]
    [Description("Read a chunk of a queued document's raw bytes as base64. Returns { contentBase64, eof, totalBytes }.")]
    public async Task<CallToolResult> McpRun(string path, long offset = 0, int length = 262144, CancellationToken ct = default)
    {
        // Only /print-queue/<filename> holds raw bytes; reject nested paths, traversal, and status.json.
        var node = PrinterQueuePath.Parse(path);
        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            return ToolResponse.Create(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.NotFound,
                Message = $"No queued document at '{path}'.",
                Retryable = false
            }.ToNode());
        }

        var (bytes, eof, total) = await spool.ReadBytesAsync(node.FileName!, offset, length, ct);
        return ToolResponse.Create(FsResultContract.ToNode(new FsBlobReadResult
        {
            ContentBase64 = Convert.ToBase64String(bytes),
            Eof = eof,
            TotalBytes = total
        }));
    }
}