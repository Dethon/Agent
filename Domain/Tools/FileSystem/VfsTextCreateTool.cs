using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class VfsTextCreateTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "create";
    public const string Name = "text_create";

    public const string ToolDescription = """
        Creates a new text file.
        The file must not already exist unless overwrite is set to true.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path for the new file (e.g., /library/notes/new-topic.md)")]
        string filePath,
        [Description("Initial content for the file")]
        string content,
        [Description("Overwrite if file already exists (default: false)")]
        bool overwrite = false,
        [Description("Create parent directories if they don't exist (default: true)")]
        bool createDirectories = true,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(filePath);
        return await resolution.Backend.CreateAsync(resolution.RelativePath, content, overwrite, createDirectories, cancellationToken);
    }
}
