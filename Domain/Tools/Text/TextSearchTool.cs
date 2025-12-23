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

        var pattern = regex ? new Regex(query, RegexOptions.IgnoreCase) : null;
        var results = new List<JsonObject>();
        var filesSearched = 0;
        var totalMatches = 0;

        var files = Directory
            .EnumerateFiles(fullPath, filePattern ?? "*", SearchOption.AllDirectories)
            .Where(f => allowedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

        foreach (var file in files)
        {
            filesSearched++;
            var fileMatches = SearchFile(file, query, pattern, contextLines, maxResults - results.Count);

            if (fileMatches.Count > 0)
            {
                totalMatches += fileMatches.Count;
                results.Add(new JsonObject
                {
                    ["file"] = Path.GetRelativePath(vaultPath, file).Replace('\\', '/'),
                    ["matches"] = new JsonArray(fileMatches.ToArray<JsonNode>())
                });

                if (results.Sum(r => r["matches"]!.AsArray().Count) >= maxResults)
                {
                    break;
                }
            }
        }

        return new JsonObject
        {
            ["query"] = query,
            ["regex"] = regex,
            ["path"] = path,
            ["filesSearched"] = filesSearched,
            ["filesWithMatches"] = results.Count,
            ["totalMatches"] = totalMatches,
            ["truncated"] = totalMatches > maxResults,
            ["results"] = new JsonArray(results.ToArray<JsonNode>())
        };
    }

    private static List<JsonObject> SearchFile(string filePath, string query, Regex? pattern, int contextLines,
        int maxMatches)
    {
        var matches = new List<JsonObject>();

        try
        {
            var lines = File.ReadAllLines(filePath);

            for (var i = 0; i < lines.Length && matches.Count < maxMatches; i++)
            {
                var line = lines[i];
                var isMatch = pattern?.IsMatch(line) ?? line.Contains(query, StringComparison.OrdinalIgnoreCase);

                if (!isMatch)
                {
                    continue;
                }

                var matchObj = new JsonObject
                {
                    ["line"] = i + 1,
                    ["text"] = line.Length > 200 ? line[..200] + "..." : line
                };

                if (contextLines > 0)
                {
                    var before = lines
                        .Skip(Math.Max(0, i - contextLines))
                        .Take(Math.Min(contextLines, i))
                        .Select(l => l.Length > 100 ? l[..100] + "..." : l)
                        .ToList();

                    var after = lines
                        .Skip(i + 1)
                        .Take(contextLines)
                        .Select(l => l.Length > 100 ? l[..100] + "..." : l)
                        .ToList();

                    if (before.Count > 0 || after.Count > 0)
                    {
                        matchObj["context"] = new JsonObject
                        {
                            ["before"] = new JsonArray(before.Select(b => JsonValue.Create(b)).ToArray<JsonNode>()),
                            ["after"] = new JsonArray(after.Select(a => JsonValue.Create(a)).ToArray<JsonNode>())
                        };
                    }
                }

                // Try to find nearest markdown heading for context
                var nearestHeading = FindNearestHeading(lines, i);
                if (nearestHeading is not null)
                {
                    matchObj["section"] = nearestHeading;
                }

                matches.Add(matchObj);
            }
        }
        catch (Exception)
        {
            // Skip files that can't be read
        }

        return matches;
    }

    private static string? FindNearestHeading(string[] lines, int lineIndex)
    {
        for (var i = lineIndex; i >= 0; i--)
        {
            if (lines[i].StartsWith('#'))
            {
                return lines[i].TrimStart('#').Trim();
            }
        }

        return null;
    }

    private string ResolvePath(string path)
    {
        var normalized = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var fullPath = string.IsNullOrEmpty(normalized)
            ? vaultPath
            : Path.GetFullPath(Path.Combine(vaultPath, normalized));

        if (!fullPath.StartsWith(vaultPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Access denied: path must be within vault directory");
        }

        return fullPath;
    }
}