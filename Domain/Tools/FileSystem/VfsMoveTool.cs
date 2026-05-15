using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

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
        var src = registry.Resolve(sourcePath);
        var dst = registry.Resolve(destinationPath);

        var info = await src.Backend.InfoAsync(src.RelativePath, cancellationToken);
        var isDirectory = info["isDirectory"]?.GetValue<bool>() == true;

        if (isDirectory)
        {
            return await VfsCopyTool.TransferDirectoryAsync(src, dst, sourcePath, destinationPath,
                overwrite, createDirectories, deleteSource: true, cancellationToken);
        }

        return await VfsCopyTool.TransferFileAsync(src, dst, sourcePath, destinationPath,
            overwrite, createDirectories, deleteSource: true, cancellationToken);
    }
}