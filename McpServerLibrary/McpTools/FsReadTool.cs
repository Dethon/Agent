using System.ComponentModel;
using Domain.Tools;
using Domain.Tools.Downloads.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsReadTool(DownloadsOverlay downloads)
{
    [McpServerTool(Name = "fs_read")]
    [Description("Read a download's virtual status file (downloads/<id>/status.json — live state, progress, eta). " +
                 "Other media files are not text-readable; use fs_blob_read for raw bytes.")]
    public async Task<CallToolResult> McpRun(
        string path, int? offset = null, int? limit = null, string? filesystem = null,
        CancellationToken ct = default)
    {
        if (LibraryFilesystem.Reject(filesystem) is { } error)
        {
            return ToolResponse.Create(error);
        }

        var overlay = await downloads.TryReadAsync(path, ct);
        return overlay is not null
            ? ToolResponse.Create(overlay)
            : ToolResponse.Create(ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                "fs_read on the media filesystem only reads downloads/<id>/status.json.",
                retryable: false));
    }
}