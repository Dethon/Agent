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

    public const int FileResultCap = 200;

    private readonly PathJail _jail = new(libraryPath.BaseLibraryPath);

    public async Task<FsResult<FsGlobResult>> Run(string pattern, CancellationToken cancellationToken, string? basePath = null)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return FsError.Invalid<FsGlobResult>("Pattern must not be empty.");
        }

        // A whole '..' segment, not the substring: v1..2.md is just a name. The check stays here
        // because a relative pattern is handed raw to the matcher, never resolved through the jail.
        if (HasDotDotSegment(pattern) || (basePath is not null && HasDotDotSegment(basePath)))
        {
            return FsError.Invalid<FsGlobResult>("Pattern and basePath must not contain '..' segments.");
        }

        if (Path.IsPathRooted(pattern))
        {
            if (!_jail.Contains(pattern.TrimEnd('/')))
            {
                return FsError.Invalid<FsGlobResult>("Absolute pattern must be under the mount root.");
            }

            var dirsOnly = pattern.EndsWith('/');
            pattern = Path.GetRelativePath(_jail.Root, pattern).TrimEnd('/') + (dirsOnly ? "/" : "");
        }

        var matcherRoot = string.IsNullOrEmpty(basePath)
            ? _jail.Root
            : Path.GetFullPath(Path.Combine(_jail.Root, basePath.TrimStart('/')));

        if (!_jail.Contains(matcherRoot))
        {
            return FsError.Invalid<FsGlobResult>("basePath must resolve under the mount root.");
        }

        string[] result;
        try
        {
            result = await client.Glob(matcherRoot, pattern, cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            return FsError.NotFound<FsGlobResult>(basePath ?? matcherRoot);
        }
        catch (ArgumentException ex)
        {
            // The client refuses a pattern it cannot expand (the brace-expansion cap); surface it
            // as the invalid-pattern envelope, like every other bad pattern.
            return FsError.Invalid<FsGlobResult>(ex.Message);
        }

        // Return entries relative to the mount root (the disk client yields absolute paths). The
        // agent-side VFS tool re-prefixes the mount point, so every filesystem speaks one format.
        var relative = result.Select(p => ToMountRelative(_jail.Root, p)).ToArray();
        var capped = relative.Length > FileResultCap;

        return new FsResult<FsGlobResult>.Ok(new FsGlobResult
        {
            Entries = capped ? relative.Take(FileResultCap).ToArray() : relative,
            Truncated = capped,
            Total = relative.Length
        });
    }

    private static bool HasDotDotSegment(string path) =>
        path.Split('/', '\\').Any(segment => segment == "..");

    private static string ToMountRelative(string baseRoot, string absolute)
    {
        var isDirectory = absolute.EndsWith('/');
        var relative = Path.GetRelativePath(baseRoot, absolute.TrimEnd('/')).Replace('\\', '/');
        return isDirectory ? relative + "/" : relative;
    }
}