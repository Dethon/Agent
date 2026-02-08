using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Domain.Tools.Text;

public class TextSearchTool(string vaultPath, string[] allowedExtensions)
    : TextToolBase(vaultPath, allowedExtensions)
{
    protected const string Name = "TextSearch";

    protected const string Description = """
                                         Searches for text across files in the vault, or within a single file.

                                         Returns matching files with line numbers and context.
                                         To modify matching content, use TextEdit with a text target.

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

    private record SearchParams(string Query, Regex? Pattern, int ContextLines, int MaxResults);

    private record FileMatch(string File, IReadOnlyList<MatchResult> Matches);

    private record MatchResult(
        int LineNumber,
        string Text,
        string? Section,
        IReadOnlyList<string>? ContextBefore,
        IReadOnlyList<string>? ContextAfter);

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
        var searchParams = new SearchParams(
            query,
            regex ? new Regex(query, RegexOptions.IgnoreCase) : null,
            contextLines,
            maxResults);

        if (filePath is not null)
        {
            return RunSingleFileSearch(filePath, query, regex, searchParams, outputMode);
        }

        var fullPath = ResolvePath(directoryPath);

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");
        }

        var files = EnumerateAllowedFiles(fullPath, filePattern);
        var (results, filesSearched) = SearchFiles(files, searchParams);
        var totalMatches = results.Sum(r => r.Matches.Count);

        return BuildResultJson(query, regex, directoryPath, filesSearched, results, totalMatches, maxResults,
            outputMode);
    }

    private JsonNode RunSingleFileSearch(string filePath, string query, bool regex, SearchParams searchParams,
        SearchOutputMode outputMode)
    {
        var fullPath = ValidateAndResolvePath(filePath);

        var matches = SearchSingleFile(fullPath, searchParams, searchParams.MaxResults);
        var results = matches.Count > 0
            ? [new FileMatch(ToRelativePath(fullPath), matches)]
            : new List<FileMatch>();

        return BuildResultJson(query, regex, filePath, 1, results, matches.Count, searchParams.MaxResults, outputMode);
    }

    private IEnumerable<string> EnumerateAllowedFiles(string fullPath, string? filePattern)
    {
        return Directory
            .EnumerateFiles(fullPath, filePattern ?? "*", SearchOption.AllDirectories)
            .Where(IsAllowedExtension);
    }

    private bool IsAllowedExtension(string filePath)
    {
        return AllowedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant());
    }

    private (List<FileMatch> Results, int FilesSearched) SearchFiles(
        IEnumerable<string> files, SearchParams searchParams)
    {
        var results = new List<FileMatch>();
        var filesSearched = 0;
        var totalMatches = 0;

        foreach (var file in files)
        {
            filesSearched++;
            var remaining = searchParams.MaxResults - totalMatches;
            if (remaining <= 0)
            {
                break;
            }

            var matches = SearchSingleFile(file, searchParams, remaining);
            if (matches.Count == 0)
            {
                continue;
            }

            results.Add(new FileMatch(ToRelativePath(file), matches));
            totalMatches += matches.Count;
        }

        return (results, filesSearched);
    }

    private static IReadOnlyList<MatchResult> SearchSingleFile(string filePath, SearchParams searchParams,
        int maxMatches)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            return FindMatchesInLines(lines, searchParams, maxMatches).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<MatchResult> FindMatchesInLines(
        string[] lines, SearchParams searchParams, int maxMatches)
    {
        return lines
            .Select((text, index) => (Text: text, Index: index))
            .Where(line => IsMatchingLine(line.Text, searchParams))
            .Take(maxMatches)
            .Select(line => CreateMatchResult(lines, line.Index, searchParams.ContextLines));
    }

    private static bool IsMatchingLine(string line, SearchParams searchParams)
    {
        return searchParams.Pattern?.IsMatch(line) ??
               line.Contains(searchParams.Query, StringComparison.OrdinalIgnoreCase);
    }

    private static MatchResult CreateMatchResult(string[] lines, int index, int contextLines)
    {
        return new MatchResult(
            LineNumber: index + 1,
            Text: Truncate(lines[index], 200),
            Section: FindNearestHeading(lines, index),
            ContextBefore: contextLines > 0 ? GetContextBefore(lines, index, contextLines) : null,
            ContextAfter: contextLines > 0 ? GetContextAfter(lines, index, contextLines) : null);
    }

    private static IReadOnlyList<string> GetContextBefore(string[] lines, int index, int count)
    {
        return lines
            .Take(index)
            .TakeLast(count)
            .Select(l => Truncate(l, 100))
            .ToList();
    }

    private static IReadOnlyList<string> GetContextAfter(string[] lines, int index, int count)
    {
        return lines
            .Skip(index + 1)
            .Take(count)
            .Select(l => Truncate(l, 100))
            .ToList();
    }

    private static string? FindNearestHeading(string[] lines, int lineIndex)
    {
        return lines
            .Take(lineIndex + 1)
            .Reverse()
            .FirstOrDefault(l => l.StartsWith('#'))
            ?.TrimStart('#')
            .Trim();
    }

    private static string Truncate(string text, int maxLength)
    {
        return text.Length > maxLength ? text[..maxLength] + "..." : text;
    }

    private string ToRelativePath(string fullPath)
    {
        return Path.GetRelativePath(VaultPath, fullPath).Replace('\\', '/');
    }

    private string ResolvePath(string path)
    {
        var normalized = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var fullPath = string.IsNullOrEmpty(normalized)
            ? VaultPath
            : Path.GetFullPath(Path.Combine(VaultPath, normalized));

        return fullPath.StartsWith(VaultPath, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : throw new UnauthorizedAccessException("Access denied: path must be within vault directory");
    }

    private static JsonNode BuildResultJson(
        string query,
        bool regex,
        string path,
        int filesSearched,
        List<FileMatch> results,
        int totalMatches,
        int maxResults,
        SearchOutputMode outputMode)
    {
        var resultMapper = outputMode == SearchOutputMode.FilesOnly
            ? (Func<FileMatch, JsonNode>)ToFileMatchSummaryJson
            : ToFileMatchJson;

        return new JsonObject
        {
            ["query"] = query,
            ["regex"] = regex,
            ["path"] = path,
            ["filesSearched"] = filesSearched,
            ["filesWithMatches"] = results.Count,
            ["totalMatches"] = totalMatches,
            ["truncated"] = totalMatches >= maxResults,
            ["results"] = new JsonArray(results.Select(resultMapper).ToArray())
        };
    }

    private static JsonNode ToFileMatchSummaryJson(FileMatch fileMatch)
    {
        return new JsonObject
        {
            ["file"] = fileMatch.File,
            ["matchCount"] = fileMatch.Matches.Count
        };
    }

    private static JsonNode ToFileMatchJson(FileMatch fileMatch)
    {
        return new JsonObject
        {
            ["file"] = fileMatch.File,
            ["matches"] = new JsonArray(fileMatch.Matches.Select(ToMatchResultJson).ToArray())
        };
    }

    private static JsonNode ToMatchResultJson(MatchResult match)
    {
        var obj = new JsonObject
        {
            ["line"] = match.LineNumber,
            ["text"] = match.Text
        };

        if (match.Section is not null)
        {
            obj["section"] = match.Section;
        }

        if (match.ContextBefore?.Count > 0 || match.ContextAfter?.Count > 0)
        {
            obj["context"] = new JsonObject
            {
                ["before"] = ToJsonArray(match.ContextBefore ?? []),
                ["after"] = ToJsonArray(match.ContextAfter ?? [])
            };
        }

        return obj;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> items)
    {
        return new JsonArray(items.Select(s => JsonValue.Create(s)).ToArray<JsonNode>());
    }
}