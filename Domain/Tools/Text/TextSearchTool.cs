using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Domain.Tools.Text;

public class TextSearchTool(string vaultPath, string[] allowedExtensions)
{
    protected const string Name = "TextSearch";

    protected const string Description = """
                                         Searches for text across all files in the vault.

                                         Returns matching files with line numbers and context. Use this to find 
                                         content when you don't know which file contains it.

                                         Parameters:
                                         - query: Text or regex pattern to search for
                                         - regex: Treat query as regex pattern (default: false)
                                         - filePattern: Glob pattern to filter files (e.g., "*.md")
                                         - path: Directory to search in (default: "/" for entire vault)
                                         - maxResults: Maximum number of matches to return (default: 50)
                                         - contextLines: Lines of context around each match (default: 1)

                                         Examples:
                                         - Find all mentions of "kubernetes": query="kubernetes"
                                         - Find TODOs: query="TODO:.*", regex=true
                                         - Search only in docs: query="api", path="/docs"
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
        string? filePattern = null,
        string path = "/",
        int maxResults = 50,
        int contextLines = 1)
    {
        var fullPath = ResolvePath(path);

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {path}");
        }

        var searchParams = new SearchParams(
            query,
            regex ? new Regex(query, RegexOptions.IgnoreCase) : null,
            contextLines,
            maxResults);

        var files = EnumerateAllowedFiles(fullPath, filePattern);
        var (results, filesSearched) = SearchFiles(files, searchParams);
        var totalMatches = results.Sum(r => r.Matches.Count);

        return BuildResultJson(query, regex, path, filesSearched, results, totalMatches, maxResults);
    }

    private IEnumerable<string> EnumerateAllowedFiles(string fullPath, string? filePattern)
    {
        return Directory
            .EnumerateFiles(fullPath, filePattern ?? "*", SearchOption.AllDirectories)
            .Where(IsAllowedExtension);
    }

    private bool IsAllowedExtension(string filePath)
    {
        return allowedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant());
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

    private IReadOnlyList<MatchResult> SearchSingleFile(string filePath, SearchParams searchParams, int maxMatches)
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
        return Path.GetRelativePath(vaultPath, fullPath).Replace('\\', '/');
    }

    private string ResolvePath(string path)
    {
        var normalized = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var fullPath = string.IsNullOrEmpty(normalized)
            ? vaultPath
            : Path.GetFullPath(Path.Combine(vaultPath, normalized));

        return fullPath.StartsWith(vaultPath, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : throw new UnauthorizedAccessException("Access denied: path must be within vault directory");
    }

    private JsonNode BuildResultJson(
        string query,
        bool regex,
        string path,
        int filesSearched,
        List<FileMatch> results,
        int totalMatches,
        int maxResults)
    {
        return new JsonObject
        {
            ["query"] = query,
            ["regex"] = regex,
            ["path"] = path,
            ["filesSearched"] = filesSearched,
            ["filesWithMatches"] = results.Count,
            ["totalMatches"] = totalMatches,
            ["truncated"] = totalMatches >= maxResults,
            ["results"] = new JsonArray(results.Select(ToFileMatchJson).ToArray())
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