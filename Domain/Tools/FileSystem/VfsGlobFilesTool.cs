using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class VfsGlobFilesTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "glob";
    public const string Name = "glob";

    public const string ToolDescription = """
        Searches a filesystem for files and directories matching a glob pattern. The pattern
        alone decides what matches — there is no mode. `*` matches one path segment, `**`
        recurses, `?` matches one character. A trailing slash restricts the match to
        directories (e.g. `*/`, `src/**/`); without it, both files and directories match.
        Directory results are returned with a trailing slash so you can tell them apart; files
        are not. Results are lexically sorted and capped at 200 on file mounts; the response is
        `{entries, truncated, total}`. An empty result means nothing matched the pattern.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual base path to search from (e.g., /library or /library/docs)")]
        string basePath,
        [Description("Glob pattern. `*` = one segment, `**` = recursive, `?` = one char. "
            + "A trailing slash (e.g. `*/`, `src/**/`) matches directories only; otherwise files "
            + "and directories both match, with directory results marked by a trailing slash.")]
        string pattern,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(basePath);
        return (await resolution.Backend.GlobAsync(resolution.RelativePath, pattern, cancellationToken)).ToNode();
    }
}