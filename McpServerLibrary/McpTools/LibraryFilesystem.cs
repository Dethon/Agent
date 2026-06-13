using System.Text.Json.Nodes;
using Domain.Tools;
using Domain.Tools.Downloads.Vfs;

namespace McpServerLibrary.McpTools;

// The library server serves a single filesystem ('media'); fs_* calls carry the target
// filesystem name, so anything else is rejected up front.
public static class LibraryFilesystem
{
    public static JsonNode? Reject(string? filesystem) =>
        filesystem is null || filesystem == MediaFilesystem.Name
            ? null
            : ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                $"Unknown filesystem '{filesystem}'. The library server only serves the '{MediaFilesystem.Name}' filesystem.",
                retryable: false);

    public static JsonNode VirtualPathError() =>
        ToolError.Create(
            ToolError.Codes.UnsupportedOperation,
            "status.json is a virtual read-only file; read it with fs_read — it cannot be moved, copied, or written.",
            retryable: false);
}