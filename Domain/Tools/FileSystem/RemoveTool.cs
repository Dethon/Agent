using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class RemoveTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "remove";
    public const string Name = "remove";

    public const string ToolDescription = """
        Removes a file or directory by moving it to a trash folder.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to file or directory to remove")]
        string path,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(path);
        return await resolution.Backend.DeleteAsync(resolution.RelativePath, cancellationToken);
    }
}
