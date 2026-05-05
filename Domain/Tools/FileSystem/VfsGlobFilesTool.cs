using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.FileSystem;

public class VfsGlobFilesTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "glob";
    public const string Name = "glob_files";

    public const string ToolDescription = """
        Searches for files or directories matching a glob pattern.
        Supports * (single segment), ** (recursive), and ? (single char).
        Use mode 'directories' to explore structure first, then 'files' with specific patterns.
        In files mode, results are capped at 200.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual base path to search from (e.g., /library or /library/docs)")]
        string basePath,
        [Description("Glob pattern (e.g., **/*.md)")]
        string pattern,
        [Description("Whether to match files or directories")]
        VfsGlobMode mode = VfsGlobMode.Directories,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(basePath);
        return await resolution.Backend.GlobAsync(resolution.RelativePath, pattern, mode, cancellationToken);
    }
}
