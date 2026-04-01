using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class ListTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "list";
    public const string Name = "list_directory";

    public const string ToolDescription = """
        Lists the contents of a directory (non-recursive).
        Returns file names, types, and sizes.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to directory (e.g., /library/docs)")]
        string path,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(path);
        return await resolution.Backend.ListAsync(resolution.RelativePath, cancellationToken);
    }
}
