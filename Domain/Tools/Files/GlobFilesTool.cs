using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;

namespace Domain.Tools.Files;

public class GlobFilesTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    protected const string Description = """
                                         Searches for files and directories matching a glob pattern relative to the mount root.
                                         `*` matches one path segment, `**` recurses, `?` matches one character.
                                         Brace alternation expands too: `**/*.{jpg,png,gif}` matches any of the listed extensions.
                                         A trailing slash matches directories only (e.g. `*/`, `src/**/`); otherwise both files
                                         and directories match, with directory results returned with a trailing slash so you can
                                         tell them apart. Results are capped at 200; the response is `{entries, truncated, total}`.
                                         An empty result means nothing matched—refine the pattern.
                                         """;

    protected const int FileResultCap = 200;

    private readonly PathJail _jail = new(libraryPath.BaseLibraryPath);

    protected async Task<JsonNode> Run(string pattern, CancellationToken cancellationToken, string? basePath = null) =>
        FsResultContract.ToNode(await RunCore(pattern, cancellationToken, basePath));

    protected async Task<FsGlobResult> RunCore(string pattern, CancellationToken cancellationToken, string? basePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        if (pattern.Contains(".."))
        {
            throw new ArgumentException("Pattern must not contain '..' segments", nameof(pattern));
        }

        if (Path.IsPathRooted(pattern))
        {
            if (!_jail.Contains(pattern.TrimEnd('/')))
            {
                throw new ArgumentException("Absolute pattern must be under the library root", nameof(pattern));
            }

            var dirsOnly = pattern.EndsWith('/');
            pattern = Path.GetRelativePath(_jail.Root, pattern).TrimEnd('/');
            if (dirsOnly)
            {
                pattern += "/";
            }
        }

        var matcherRoot = ResolveMatcherRoot(basePath);
        var result = await client.Glob(matcherRoot, pattern, cancellationToken);

        // Return entries relative to the mount root (the disk client yields absolute paths). The
        // agent-side VFS tool re-prefixes the mount point, so every filesystem speaks one format.
        var relative = result.Select(p => ToMountRelative(_jail.Root, p)).ToArray();
        var capped = relative.Length > FileResultCap;

        return new FsGlobResult
        {
            Entries = capped ? relative.Take(FileResultCap).ToArray() : relative,
            Truncated = capped,
            Total = relative.Length
        };
    }

    private static string ToMountRelative(string baseRoot, string absolute)
    {
        var isDirectory = absolute.EndsWith('/');
        var relative = Path.GetRelativePath(baseRoot, absolute.TrimEnd('/')).Replace('\\', '/');
        return isDirectory ? relative + "/" : relative;
    }

    private string ResolveMatcherRoot(string? basePath)
    {
        if (string.IsNullOrEmpty(basePath))
        {
            return libraryPath.BaseLibraryPath;
        }

        if (basePath.Contains(".."))
        {
            throw new ArgumentException("basePath must not contain '..' segments", nameof(basePath));
        }

        var canonRoot = Path.GetFullPath(Path.Combine(_jail.Root, basePath.TrimStart('/')));

        return _jail.Contains(canonRoot)
            ? canonRoot
            : throw new ArgumentException("basePath must resolve under the library root", nameof(basePath));
    }
}