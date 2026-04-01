using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class TextEditTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "edit";
    public const string Name = "text_edit";

    public const string ToolDescription = """
        Edits a text file by replacing exact string matches.
        When replaceAll is false, oldString must appear exactly once.
        If multiple occurrences are found, the tool fails — provide more surrounding context in oldString to disambiguate.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to file (e.g., /library/notes/todo.md)")]
        string filePath,
        [Description("Exact text to find (case-sensitive)")]
        string oldString,
        [Description("Replacement text")]
        string newString,
        [Description("Replace all occurrences (default: false)")]
        bool replaceAll = false,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(filePath);
        return await resolution.Backend.EditAsync(resolution.RelativePath, oldString, newString, replaceAll, cancellationToken);
    }
}
