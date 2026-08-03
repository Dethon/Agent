using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Domain.DTOs.FileSystem;

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

    private sealed record Scan(string Query, Regex? Pattern, int ContextLines, int MaxResults, SearchOutputMode OutputMode);

    protected JsonNode Run(
        string query,
        bool regex = false,
        string? filePath = null,
        string? filePattern = null,
        string directoryPath = "/",
        int maxResults = 50,
        int contextLines = 1,
        SearchOutputMode outputMode = SearchOutputMode.Content)
    {
        var scan = new Scan(
            query,
            regex ? new Regex(query, RegexOptions.IgnoreCase) : null,
            contextLines,
            maxResults,
            outputMode);

        return filePath is not null
            ? FsResultContract.ToNode(SearchOneFile(filePath, regex, scan))
            : FsResultContract.ToNode(SearchDirectory(directoryPath, filePattern, regex, scan));
    }

    private FsSearchResult SearchOneFile(string filePath, bool regex, Scan scan)
    {
        var fullPath = ValidateAndResolvePath(filePath);
        var matches = MatchesIn(fullPath, scan, scan.MaxResults);

        return Build(filePath, regex, scan, filesSearched: 1, matches.Count == 0
            ? []
            : [BuildFileResult(ToRelativePath(fullPath), matches, scan.OutputMode)], matches.Count);
    }

    private FsSearchResult SearchDirectory(string directoryPath, string? filePattern, bool regex, Scan scan)
    {
        var fullPath = ResolvePath(directoryPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");
        }

        var results = new List<FsSearchFileResult>();
        var filesSearched = 0;
        var totalMatches = 0;

        foreach (var file in EnumerateAllowedFiles(fullPath, filePattern))
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

    private static FsSearchResult Build(
        string path, bool regex, Scan scan, int filesSearched,
        IReadOnlyList<FsSearchFileResult> results, int totalMatches) =>
        new()
        {
            Query = scan.Query,
            Regex = regex,
            Path = path,
            FilesSearched = filesSearched,
            FilesWithMatches = results.Count,
            TotalMatches = totalMatches,
            Truncated = totalMatches >= scan.MaxResults,
            Results = results
        };

    private static FsSearchFileResult BuildFileResult(
        string file, IReadOnlyList<FsSearchMatch> matches, SearchOutputMode outputMode) =>
        outputMode == SearchOutputMode.FilesOnly
            ? new FsSearchFileResult { File = file, MatchCount = matches.Count }
            : new FsSearchFileResult { File = file, Matches = matches };

    private IEnumerable<string> EnumerateAllowedFiles(string fullPath, string? filePattern) =>
        Directory
            .EnumerateFiles(fullPath, filePattern ?? "*", SearchOption.AllDirectories)
            .Where(IsAllowedExtension);

    private bool IsAllowedExtension(string filePath) =>
        AllowedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant());

    // An unreadable file is not a failure of the search — skip it and keep scanning the rest.
    private static IReadOnlyList<FsSearchMatch> MatchesIn(string filePath, Scan scan, int maxMatches)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(filePath);
        }
        catch
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

    private static bool IsMatchingLine(string line, Scan scan) =>
        scan.Pattern?.IsMatch(line) ?? line.Contains(scan.Query, StringComparison.OrdinalIgnoreCase);

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
    private string ResolvePath(string path)
    {
        var normalized = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return Jail.Guard(string.IsNullOrEmpty(normalized)
            ? Jail.Root
            : Path.GetFullPath(Path.Combine(Jail.Root, normalized)));
    }
}