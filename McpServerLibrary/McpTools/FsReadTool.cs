using System.ComponentModel;
using Domain.Tools;
using Domain.Tools.Downloads.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsReadTool(DownloadsFileSystem downloads)
{
    [McpServerTool(Name = "fs_read")]
    [Description("Read a downloads filesystem file (<id>/status.json)")]
    public async Task<CallToolResult> McpRun(
        string path, int? offset = null, int? limit = null, string? filesystem = null,
        CancellationToken ct = default)
        => filesystem == downloads.FilesystemName
            ? ToolResponse.Create(await downloads.ReadAsync(path, offset, limit, ct))
            : ToolResponse.Create(ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                "fs_read on the library server is only available for the downloads filesystem.",
                retryable: false));
}