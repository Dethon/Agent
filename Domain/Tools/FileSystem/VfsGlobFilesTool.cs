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
        Searches for files OR directories matching a glob pattern. You MUST set `mode` to match
        what you want — the two are disjoint. `mode: files` returns only files; `mode: directories`
        (the default) returns only directories. A file pattern like `*.sh` returns NOTHING in
        directories mode, and directories never appear in files mode — so an empty result almost
        always means the wrong mode: switch the mode before you touch the pattern.
        Supports * (single segment), ** (recursive), and ? (single char).
        Typical flow: `directories` to explore structure first, then `files` with a specific pattern.
        In files mode, results are capped at 200; the response is `{entries, truncated, total}`.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual base path to search from (e.g., /library or /library/docs)")]
        string basePath,
        [Description("Glob pattern (e.g., **/*.md)")]
        string pattern,
        [Description("Pick deliberately: 'files' returns only files, 'directories' returns only "
            + "directories (the default). They never overlap, so an empty result usually means "
            + "the wrong mode, not a missing match.")]
        VfsGlobMode mode = VfsGlobMode.Directories,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(basePath);
        return await resolution.Backend.GlobAsync(resolution.RelativePath, pattern, mode, cancellationToken);
    }
}