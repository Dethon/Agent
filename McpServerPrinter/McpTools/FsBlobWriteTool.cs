using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsBlobWriteTool(IPrintSpool spool)
{
    [McpServerTool(Name = "fs_blob_write")]
    [Description("Write a chunk of raw bytes (base64) to a queued document. offset=0 starts it; the document prints once writes go quiet.")]
    public async Task<CallToolResult> McpRun(
        string path, string contentBase64, long offset = 0, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default)
    {
        var fileName = path.TrimStart('/');
        var bytes = Convert.FromBase64String(contentBase64);
        await spool.WriteBytesAsync(fileName, "application/octet-stream", bytes, offset, overwrite, ct);
        var entry = await spool.GetAsync(fileName, ct);
        return ToolResponse.Create(FsResultContract.ToNode(new FsBlobWriteResult
        {
            Path = path,
            BytesWritten = bytes.Length,
            TotalBytes = entry?.SizeBytes ?? bytes.Length
        }));
    }
}