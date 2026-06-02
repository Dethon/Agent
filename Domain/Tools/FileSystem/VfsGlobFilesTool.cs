using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;

namespace Domain.Tools.FileSystem;

public class VfsGlobFilesTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "glob";
    public const string Name = "glob";

    public const string ToolDescription = """
        Searches a filesystem for files and directories matching a glob pattern. The pattern
        alone decides what matches — there is no mode. `*` matches one path segment, `**`
        recurses, `?` matches one character. Brace alternation expands too:
        `**/*.{jpg,png,gif}` matches any of the listed extensions. A trailing slash restricts the match to
        directories (e.g. `*/`, `src/**/`); without it, both files and directories match.
        Directory results are returned with a trailing slash so you can tell them apart; files
        are not. Entries are full virtual paths (including the mount point), ready to pass straight
        to other filesystem tools. Results are lexically sorted and capped at 200 on file mounts;
        the response is `{entries, truncated, total}`. An empty result means nothing matched.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual base path to search from (e.g., /library or /library/docs)")]
        string basePath,
        [Description("Glob pattern. `*` = one segment, `**` = recursive, `?` = one char, "
            + "`{a,b}` = brace alternation (e.g. `**/*.{jpg,png}`). "
            + "A trailing slash (e.g. `*/`, `src/**/`) matches directories only; otherwise files "
            + "and directories both match, with directory results marked by a trailing slash.")]
        string pattern,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(basePath);
        var result = await resolution.Backend.GlobAsync(resolution.RelativePath, pattern, cancellationToken);
        return Normalize(result, resolution.MountPoint).ToNode();
    }

    // Backends return entries relative to their mount root (with varying leading-slash conventions).
    // Prefixing the mount point here yields a single, uniform full-virtual-path format across every
    // filesystem — directly reusable as input to read/edit/etc. Directory markers (trailing slash)
    // are preserved because only the leading slash is trimmed.
    private static FsResult<FsGlobResult> Normalize(FsResult<FsGlobResult> result, string mountPoint) =>
        result is FsResult<FsGlobResult>.Ok ok
            ? new FsResult<FsGlobResult>.Ok(ok.Value with
            {
                Entries = ok.Value.Entries.Select(e => $"{mountPoint.TrimEnd('/')}/{e.TrimStart('/')}").ToList()
            })
            : result;
}