using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class VfsRemoveTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "remove";
    public const string Name = "remove";

    public const string ToolDescription = """
        Removes a file or directory.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to file or directory to remove")]
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!registry.Resolve(path).TryGetValue(out var resolution, out var unresolved))
        {
            return unresolved.ToNode();
        }

        return (await resolution.Backend.DeleteAsync(resolution.RelativePath, cancellationToken)).ToNode();
    }
}