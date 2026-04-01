using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class MoveTool(IVirtualFileSystemRegistry registry)
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
            throw new InvalidOperationException(
                "Cannot move between different filesystems. Source and destination must be on the same filesystem.");
        }

        return await sourceResolution.Backend.MoveAsync(
            sourceResolution.RelativePath, destResolution.RelativePath, cancellationToken);
    }
}
