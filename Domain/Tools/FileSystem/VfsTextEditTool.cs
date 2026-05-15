using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.FileSystem;

public class VfsTextEditTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "edit";
    public const string Name = "text_edit";

    public const string ToolDescription = """
        Edits a text file by applying a non-empty list of edits in order, atomically.
        Each edit has oldString (exact, case-sensitive), newString, and replaceAll (default false).
        Edits are applied sequentially — edit N sees the result of edits 1…N-1.
        If any edit fails (oldString not found, or multiple matches without replaceAll), the file is not written.
        When replaceAll is false, oldString must appear exactly once at that point in the sequence.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to file (e.g., /library/notes/todo.md)")]
        string filePath,
        [Description("Edits to apply in order, atomically. Must be non-empty.")]
        IReadOnlyList<TextEdit> edits,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(filePath);
        return await resolution.Backend.EditAsync(resolution.RelativePath, edits, cancellationToken);
    }
}