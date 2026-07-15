using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

public class VfsTextCreateTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "create";
    public const string Name = "text_create";

    private const string CoercionNote =
        "The 'content' argument arrived as structured JSON rather than a string; its JSON text was written. Pass 'content' as a string next time.";

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
        AIFunctionArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(filePath);
        var result = await resolution.Backend.CreateAsync(
            resolution.RelativePath, content, overwrite, createDirectories, cancellationToken);

        if (TextArg.WasCoercedArg(arguments, "content") && result.TryGetValue(out var value, out _))
        {
            return FsResultContract.ToNode(value with { Note = CoercionNote });
        }

        return result.ToNode();
    }
}