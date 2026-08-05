using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;

namespace Domain.Tools.FileSystem;

public class VfsMoveTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "move";
    public const string Name = "move";

    public const string ToolDescription = """
        Moves and/or renames a file or directory. Source and destination can be on the same
        filesystem (atomic native move) or on different filesystems (streamed copy + delete; not atomic).
        Directory sources are handled recursively for cross-FS moves.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to source file or directory")] string sourcePath,
        [Description("Virtual path to destination")] string destinationPath,
        [Description("Overwrite destination if it exists (default: false)")] bool overwrite = false,
        [Description("Create destination parent directories if missing (default: true)")] bool createDirectories = true,
        CancellationToken cancellationToken = default)
    {
        if (!registry.Resolve(sourcePath).TryGetValue(out var src, out var unresolvedSource))
        {
            return unresolvedSource.ToNode();
        }

        if (!registry.Resolve(destinationPath).TryGetValue(out var dst, out var unresolvedDestination))
        {
            return unresolvedDestination.ToNode();
        }

        if (!ReferenceEquals(src.Backend, dst.Backend) &&
            await CrossMountRefusalAsync(src, dst, cancellationToken) is { } refusal)
        {
            return refusal.ToNode();
        }

        var infoResult = await src.Backend.InfoAsync(src.RelativePath, cancellationToken);
        if (!infoResult.TryGetValue(out var info, out var infoError))
        {
            return infoError.ToNode();
        }
        var isDirectory = info.IsDirectory == true;

        if (isDirectory)
        {
            return await VfsCopyTool.TransferDirectoryAsync(src, dst, sourcePath, destinationPath,
                overwrite, createDirectories, deleteSource: true, cancellationToken);
        }

        return await VfsCopyTool.TransferFileAsync(src, dst, sourcePath, destinationPath,
            overwrite, createDirectories, deleteSource: true, cancellationToken);
    }

    // A cross-mount move is a streamed copy followed by a delete of the source, so the source
    // backend's own MoveAsync — where a refusal like "this path belongs to a live download" lives —
    // is never called. Both ends are asked here, before the first byte moves, so a refused move
    // leaves no partial copy behind.
    private static async Task<ToolErrorResult?> CrossMountRefusalAsync(
        FileSystemResolution src, FileSystemResolution dst, CancellationToken ct) =>
        await RefusalAsync(src, ct) ?? await RefusalAsync(dst, ct);

    private static Task<ToolErrorResult?> RefusalAsync(FileSystemResolution end, CancellationToken ct) =>
        end.Backend is ICrossMountMoveGuard guard
            ? guard.RefuseMoveAsync(end.RelativePath, ct)
            : Task.FromResult<ToolErrorResult?>(null);
}