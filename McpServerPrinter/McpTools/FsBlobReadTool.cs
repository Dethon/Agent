using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
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
        var fileName = path.TrimStart('/');
        var (bytes, eof, total) = await spool.ReadBytesAsync(fileName, offset, length, ct);
        return ToolResponse.Create(FsResultContract.ToNode(new FsBlobReadResult
        {
            ContentBase64 = Convert.ToBase64String(bytes),
            Eof = eof,
            TotalBytes = total
        }));
    }
}