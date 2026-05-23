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
                                         A trailing slash matches directories only (e.g. `*/`, `src/**/`); otherwise both files
                                         and directories match, with directory results returned with a trailing slash so you can
                                         tell them apart. Results are capped at 200; the response is `{entries, truncated, total}`.
                                         An empty result means nothing matched—refine the pattern.
                                         """;

    private const int FileResultCap = 200;

    protected async Task<JsonNode> Run(string pattern, CancellationToken cancellationToken, string? basePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        if (pattern.Contains(".."))
        {
            throw new ArgumentException("Pattern must not contain '..' segments", nameof(pattern));
        }

        if (Path.IsPathRooted(pattern))
        {
            if (!pattern.StartsWith(libraryPath.BaseLibraryPath, StringComparison.Ordinal))
            {
                throw new ArgumentException("Absolute pattern must be under the library root", nameof(pattern));
            }

            var dirsOnly = pattern.EndsWith('/');
            pattern = Path.GetRelativePath(libraryPath.BaseLibraryPath, pattern).TrimEnd('/');
            if (dirsOnly)
            {
                pattern += "/";
            }
        }

        var matcherRoot = ResolveMatcherRoot(basePath);
        var result = await client.Glob(matcherRoot, pattern, cancellationToken);
        var capped = result.Length > FileResultCap;

        return FsResultContract.ToNode(new FsGlobResult
        {
            Entries = capped ? result.Take(FileResultCap).ToArray() : result,
            Truncated = capped,
            Total = result.Length
        });
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

        var combined = Path.Combine(libraryPath.BaseLibraryPath, basePath.TrimStart('/'));
        var canonRoot = Path.GetFullPath(combined);
        var canonBase = Path.GetFullPath(libraryPath.BaseLibraryPath);

        if (!canonRoot.StartsWith(canonBase, StringComparison.Ordinal))
        {
            throw new ArgumentException("basePath must resolve under the library root", nameof(basePath));
        }

        return canonRoot;
    }
}