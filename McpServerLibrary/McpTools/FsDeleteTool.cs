using System.ComponentModel;
using Domain.Tools.Downloads.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsDeleteTool(DownloadsOverlay downloads)
{
    [McpServerTool(Name = "fs_delete")]
    [Description("Delete a download directory (downloads/<id>): cancels the torrent task and cleans up its files. " +
                 "Also removes leftover download directories whose torrent is already gone. " +
                 "Other media paths cannot be deleted.")]
    public async Task<CallToolResult> McpRun(
        string path, string? filesystem = null, CancellationToken ct = default)
    {
        if (LibraryFilesystem.Reject(filesystem) is { } error)
        {
            return ToolResponse.Create(error);
        }

        return ToolResponse.Create(await downloads.DeleteAsync(path, ct));
    }
}