using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class VfsMoveTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "move";
    public const string Name = "move";

    public const string ToolDescription = """
        Moves and/or renames a file or directory.
        Both source and destination must be on the same filesystem.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to source file or directory")]
        string sourcePath,
        [Description("Virtual path to destination")]
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var sourceResolution = registry.Resolve(sourcePath);
        var destResolution = registry.Resolve(destinationPath);

        if (sourceResolution.Backend != destResolution.Backend)
        {
            return ToolError.Create(
                ToolError.Codes.CrossFilesystem,
                $"Cannot move between different filesystems. " +
                $"Source is on '{sourceResolution.Backend.FilesystemName}', " +
                $"destination is on '{destResolution.Backend.FilesystemName}'. " +
                $"Both paths must be on the same filesystem.",
                retryable: false,
                hint: "Copy the file across mounts manually (read from source, create at destination), then remove the source.");
        }

        return await sourceResolution.Backend.MoveAsync(
            sourceResolution.RelativePath, destResolution.RelativePath, cancellationToken);
    }
}
