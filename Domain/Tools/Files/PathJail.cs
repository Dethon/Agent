using System.Diagnostics.CodeAnalysis;

namespace Domain.Tools.Files;

// One decision about whether a path is inside a mount root, built once from the canonical root.
// A prefix counts as containment only when a separator follows it, so a sibling directory whose
// name merely extends the root's — /library-backup under /library — is outside. One comparison
// rule applies everywhere, replacing the three the hand-written copies had drifted into.
//
// The prefix check alone is lexical, and a symlink physically inside the root can point outside
// it — reading through one would serve foreign bytes. So containment also resolves every existing
// component (a leaf that does not exist yet is judged by where its parent physically lives) and
// holds the resolved path to the same prefix rule, against the root's own physical location.
public sealed class PathJail
{
    private const StringComparison _comparison = StringComparison.Ordinal;

    private readonly string _rootWithSeparator;
    private readonly Lazy<(string Root, string WithSeparator)> _physicalRoot;

    public PathJail(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        // A root that is itself the filesystem root already ends in a separator; appending another
        // would make every path under it look outside.
        _rootWithSeparator = WithSeparator(Root);
        // Resolved lazily so a jail can be built before its root directory exists, and resolved at
        // all so a root reached through a symlink still admits its own contents.
        _physicalRoot = new Lazy<(string, string)>(() =>
        {
            var physical = ResolvePhysical(Root);
            return (physical, WithSeparator(physical));
        });
    }

    public string Root { get; }

    public string DeniedMessage => $"Access denied: path must be within {Root}";

    public bool Contains(string fullPath) =>
        IsUnder(fullPath, Root, _rootWithSeparator) &&
        TryResolvePhysical(Path.TrimEndingDirectorySeparator(fullPath), out var physical) &&
        IsUnder(physical, _physicalRoot.Value.Root, _physicalRoot.Value.WithSeparator);

    // A path whose physical location cannot be established — a symlink cycle has no final target,
    // and resolving one throws — is not a path this jail can vouch for, so it is outside. Callers
    // ask this before every operation and already answer "denied"; an exception here would come out
    // of a tool instead of the envelope the prompt promises.
    private static bool TryResolvePhysical(string fullPath, out string physical)
    {
        try
        {
            physical = ResolvePhysical(fullPath);
            return true;
        }
        catch (IOException)
        {
            physical = string.Empty;
            return false;
        }
    }

    private static bool IsUnder(string fullPath, string root, string rootWithSeparator) =>
        fullPath.Equals(root, _comparison) || fullPath.StartsWith(rootWithSeparator, _comparison);

    private static string WithSeparator(string root) =>
        root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

    // The physical location of a path: every existing component resolved to its final link target,
    // components that do not exist (yet) carried through unchanged.
    private static string ResolvePhysical(string fullPath)
    {
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent))
        {
            return fullPath;
        }

        var combined = Path.Combine(ResolvePhysical(parent), Path.GetFileName(fullPath));
        var info = Directory.Exists(combined)
            ? (FileSystemInfo)new DirectoryInfo(combined)
            : new FileInfo(combined);
        return info.LinkTarget is null
            ? combined
            : info.ResolveLinkTarget(returnFinalTarget: true)!.FullName;
    }

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

}