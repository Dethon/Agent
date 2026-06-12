using System.ComponentModel;
using Domain.Tools;
using Domain.Tools.Downloads.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsDeleteTool(DownloadsFileSystem downloads)
{
    [McpServerTool(Name = "fs_delete")]
    [Description("Delete a download directory: cancels/removes the torrent task and cleans up its files")]
    public async Task<CallToolResult> McpRun(
        string path, string? filesystem = null, CancellationToken ct = default)
        => filesystem == downloads.FilesystemName
            ? ToolResponse.Create(await downloads.DeleteAsync(path, ct))
            : ToolResponse.Create(ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                "fs_delete on the library server is only available for the downloads filesystem.",
                retryable: false));
}