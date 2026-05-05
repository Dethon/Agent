using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class VfsFileInfoTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "info";
    public const string Name = "file_info";

    public const string ToolDescription = """
        Returns metadata about a path: exists, isDirectory, size (files only), and lastModified.
        Cheap existence/metadata check — use before read/edit/move/delete to avoid errors on missing paths.
        Works for both files and directories. Never throws on missing paths; returns exists=false instead.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to file or directory (e.g., /library/notes/todo.md)")]
        string path,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(path);
        return await resolution.Backend.InfoAsync(resolution.RelativePath, cancellationToken);
    }
}
