using System.Text.RegularExpressions;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Text;

public class TextSearchTool(string vaultPath, string[] allowedExtensions)
    : TextToolBase(vaultPath, allowedExtensions)
{
    protected const string Description = """
                                         Searches for text across files in the vault, or within a single file.

                                         Returns matching files with line numbers and context.
                                         To modify matching content, use the edit tool with a text target.

                                         Parameters:
                                         - query: Text or regex pattern to search for
                                         - regex: Treat query as regex pattern (default: false)
                                         - filePath: Optional. Search within this single file only (ignores directoryPath and filePattern)
                                         - filePattern: Glob pattern to filter files (e.g., "*.md")
                                         - directoryPath: Directory to search in (default: "/" for entire vault)
                                         - maxResults: Maximum number of matches to return (default: 50)
                                         - contextLines: Lines of context around each match (default: 1)

                                         Examples:
                                         - Find all mentions of "kubernetes": query="kubernetes"
                                         - Find in single file: query="config", filePath="docs/setup.md"
                                         - Find TODOs: query="TODO:.*", regex=true
                                         - Search only in docs: query="api", directoryPath="/docs"
                                         - Search markdown files: query="config", filePattern="*.md"
                                         """;

    private sealed record Scan(string Query, Regex Pattern, int ContextLines, int MaxResults, VfsTextSearchOutputMode OutputMode);

    // The same bounded matcher every other filesystem uses: a pattern that cannot compile comes
    // back as an envelope, and one that backtracks catastrophically ends as a timeout.
    private static readonly TimeSpan _matchTimeout = TimeSpan.FromSeconds(1);

    public FsResult<FsSearchResult> Run(
        string query,
        bool regex = false,
        string? filePath = null,
        string? filePattern = null,
        string directoryPath = "/",
        int maxResults = 50,
        int contextLines = 1,
        VfsTextSearchOutputMode outputMode = VfsTextSearchOutputMode.Content)
    {
        if (!SearchRegex.Compile(query, regex, _matchTimeout).TryGetValue(out var pattern, out var patternError))
        {
            return new FsResult<FsSearchResult>.Err(patternError);
        }

        var scan = new Scan(query, pattern, contextLines, maxResults, outputMode);

        try
        {
            return filePath is not null
                ? SearchOneFile(filePath, regex, scan)
                : SearchDirectory(directoryPath, filePattern, regex, scan);
        }
        catch (RegexMatchTimeoutException)
        {
            return new FsResult<FsSearchResult>.Err(SearchRegex.TimedOut(query));
        }
    }

    private FsResult<FsSearchResult> SearchOneFile(string filePath, bool regex, Scan scan)
    {
        if (!ResolveExistingFile(filePath).TryGetValue(out var fullPath, out var resolveError))
        {
            return new FsResult<FsSearchResult>.Err(resolveError);
        }

        var matches = MatchesIn(fullPath, scan, scan.MaxResults);

        return Build(filePath, regex, scan, filesSearched: 1, matches.Count == 0
            ? []
            : [BuildFileResult(ToRelativePath(fullPath), matches, scan.OutputMode)], matches.Count);
    }

    private FsResult<FsSearchResult> SearchDirectory(string directoryPath, string? filePattern, bool regex, Scan scan)
    {
        if (!ResolveDirectory(directoryPath).TryGetValue(out var fullPath, out var resolveError))
        {
            return new FsResult<FsSearchResult>.Err(resolveError);
        }

        if (!VfsContentSearch.CompileFilePattern(filePattern, _matchTimeout)
                .TryGetValue(out var matchesPattern, out var patternError))
        {
            return new FsResult<FsSearchResult>.Err(patternError);
        }

        var results = new List<FsSearchFileResult>();
        var filesSearched = 0;
        var totalMatches = 0;

        foreach (var file in EnumerateAllowedFiles(fullPath, filePattern, matchesPattern))
        {
            filesSearched++;
            var remaining = scan.MaxResults - totalMatches;
            if (remaining <= 0)
            {
                break;
            }

            var matches = MatchesIn(file, scan, remaining);
            if (matches.Count == 0)
            {
                continue;
            }

            results.Add(BuildFileResult(ToRelativePath(file), matches, scan.OutputMode));
            totalMatches += matches.Count;
        }

        return Build(directoryPath, regex, scan, filesSearched, results, totalMatches);
    }

    private static FsResult<FsSearchResult> Build(
        string path, bool regex, Scan scan, int filesSearched,
        IReadOnlyList<FsSearchFileResult> results, int totalMatches) =>
        new FsResult<FsSearchResult>.Ok(new FsSearchResult
        {
            Query = scan.Query,
            Regex = regex,
            Path = path,
            FilesSearched = filesSearched,
            FilesWithMatches = results.Count,
            TotalMatches = totalMatches,
            Truncated = totalMatches >= scan.MaxResults,
            Results = results
        });

    private static FsSearchFileResult BuildFileResult(
        string file, IReadOnlyList<FsSearchMatch> matches, VfsTextSearchOutputMode outputMode) =>
        outputMode == VfsTextSearchOutputMode.FilesOnly
            ? new FsSearchFileResult { File = file, MatchCount = matches.Count }
            : new FsSearchFileResult { File = file, Matches = matches };

    // The jail vets the search root, but a symlink discovered inside the tree can point
    // anywhere — following it would serve foreign file content as search results (or recurse
    // forever on a cycle), so the scan skips symlinks wholesale.
    private static readonly EnumerationOptions _skipSymlinks = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    // The pattern never reaches EnumerateFiles: .NET resolves a leading "../" inside a search
    // pattern, so "../*.md" would read above the vault, and a pattern naming a missing directory
    // or an absolute path throws straight past the envelope. Enumerate everything under the
    // vetted root instead and filter names here, with the same compiled matcher every other
    // filesystem uses.
    private IEnumerable<string> EnumerateAllowedFiles(
        string fullPath, string? filePattern, Func<string, bool> matchesPattern) =>
        Directory
            .EnumerateFiles(fullPath, "*", _skipSymlinks)
            .Where(IsAllowedExtension)
            .Where(file => matchesPattern(PatternCandidate(fullPath, file, filePattern)));

    // A bare pattern ("*.md") filters file names at any depth, as it did when EnumerateFiles
    // matched it per directory; a pattern with a separator ("docs/*.md") filters the path
    // relative to the searched directory.
    private static string PatternCandidate(string root, string file, string? filePattern) =>
        filePattern?.Contains('/') == true
            ? Path.GetRelativePath(root, file).Replace('\\', '/')
            : Path.GetFileName(file);

    private bool IsAllowedExtension(string filePath) =>
        AllowedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant());

    // An unreadable file is not a failure of the search — skip it and keep scanning the rest. Only
    // the read is guarded: a match timeout must reach the caller as its own envelope.
    private static IReadOnlyList<FsSearchMatch> MatchesIn(string filePath, Scan scan, int maxMatches)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return lines
            .Select((text, index) => (Text: text, Index: index))
            .Where(line => IsMatchingLine(line.Text, scan))
            .Take(maxMatches)
            .Select(line => BuildMatch(lines, line.Index, scan.ContextLines))
            .ToList();
    }

    private static bool IsMatchingLine(string line, Scan scan) => scan.Pattern.IsMatch(line);

    private static FsSearchMatch BuildMatch(string[] lines, int index, int contextLines)
    {
        var before = contextLines > 0 ? Context(lines.Take(index).TakeLast(contextLines)) : [];
        var after = contextLines > 0 ? Context(lines.Skip(index + 1).Take(contextLines)) : [];

        return new FsSearchMatch
        {
            Line = index + 1,
            Text = Truncate(lines[index], 200),
            Section = FindNearestHeading(lines, index),
            Context = before.Count > 0 || after.Count > 0
                ? new FsSearchContext { Before = before, After = after }
                : null
        };
    }

    private static IReadOnlyList<string> Context(IEnumerable<string> lines) =>
        lines.Select(l => Truncate(l, 100)).ToList();

    private static string? FindNearestHeading(string[] lines, int lineIndex) =>
        lines
            .Take(lineIndex + 1)
            .Reverse()
            .FirstOrDefault(l => l.StartsWith('#'))
            ?.TrimStart('#')
            .Trim();

    private static string Truncate(string text, int maxLength) =>
        text.Length > maxLength ? text[..maxLength] + "..." : text;

    private string ToRelativePath(string fullPath) =>
        Path.GetRelativePath(VaultPath, fullPath).Replace('\\', '/');

    // A search path is always vault-relative: a leading '/' means the vault root, not the OS root.
    // Containment is then decided by the one jail, like every other disk tool.
    private FsResult<string> ResolveDirectory(string path)
    {
        var normalized = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var fullPath = string.IsNullOrEmpty(normalized)
            ? Jail.Root
            : Path.GetFullPath(Path.Combine(Jail.Root, normalized));

        if (!Jail.Contains(fullPath))
        {
            return FsError.Invalid<string>(Jail.DeniedMessage);
        }

        return Directory.Exists(fullPath)
            ? new FsResult<string>.Ok(fullPath)
            : FsError.NotFound<string>(path);
    }
}