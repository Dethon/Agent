using System.Diagnostics.CodeAnalysis;

namespace Domain.Tools.Files;

// One decision about whether a path is inside a mount root, built once from the canonical root.
// A prefix counts as containment only when a separator follows it, so a sibling directory whose
// name merely extends the root's — /library-backup under /library — is outside. One comparison
// rule applies everywhere, replacing the three the hand-written copies had drifted into.
public sealed class PathJail
{
    private const StringComparison _comparison = StringComparison.Ordinal;

    private readonly string _rootWithSeparator;

    public PathJail(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _rootWithSeparator = Root + Path.DirectorySeparatorChar;
    }

    public string Root { get; }

    public bool Contains(string fullPath) =>
        fullPath.Equals(Root, _comparison) || fullPath.StartsWith(_rootWithSeparator, _comparison);

    // The resolution the disk tools share: an absolute path is taken as given, a relative one is
    // combined with the root, and '/' separators are accepted on any platform.
    public string Combine(string path)
    {
        var normalized = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Root, normalized));
    }

    public bool TryResolve(string path, [NotNullWhen(true)] out string? fullPath)
    {
        var candidate = Combine(path);
        fullPath = Contains(candidate) ? candidate : null;
        return fullPath is not null;
    }

    public string Resolve(string path) => Guard(Combine(path));

    // For a caller that builds the full path its own way and only needs the containment decision.
    public string Guard(string fullPath) =>
        Contains(fullPath)
            ? fullPath
            : throw new UnauthorizedAccessException($"Access denied: path must be within {Root}");
}