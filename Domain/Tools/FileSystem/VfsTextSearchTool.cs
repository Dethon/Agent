using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class VfsTextSearchTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "search";
    public const string Name = "text_search";

    public const string ToolDescription = """
        Searches for text across files in a filesystem, or within a single file.
        Returns matching files with line numbers and context.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Text or regex pattern to search for")]
        string query,
        [Description("Treat query as regex pattern (default: false)")]
        bool regex = false,
        [Description("Search within this single file only (virtual path)")]
        string? filePath = null,
        [Description("Virtual directory path to search in")]
        string? directoryPath = null,
        [Description("Glob pattern to filter files (e.g., *.md)")]
        string? filePattern = null,
        [Description("Maximum number of matches to return (default: 50)")]
        int maxResults = 50,
        [Description("Lines of context around each match (default: 1)")]
        int contextLines = 1,
        [Description("'content' or 'filesOnly' (default: 'content')")]
        string outputMode = "content",
        CancellationToken cancellationToken = default)
    {
        if (filePath is not null)
        {
            var fileResolution = registry.Resolve(filePath);
            return await fileResolution.Backend.SearchAsync(
                query, regex, fileResolution.RelativePath, null, filePattern,
                maxResults, contextLines, outputMode, cancellationToken);
        }

        if (directoryPath is null)
        {
            return ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Either filePath or directoryPath must be provided",
                retryable: false);
        }

        var dirResolution = registry.Resolve(directoryPath);
        return await dirResolution.Backend.SearchAsync(
            query, regex, null, dirResolution.RelativePath, filePattern,
            maxResults, contextLines, outputMode, cancellationToken);
    }
}
