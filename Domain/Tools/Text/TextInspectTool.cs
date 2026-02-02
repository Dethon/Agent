using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Domain.Tools.Text;

public class TextInspectTool(string vaultPath, string[] allowedExtensions) : TextToolBase(vaultPath, allowedExtensions)
{
    protected const string Name = "TextInspect";

    protected const string Description = """
                                         Inspects a text or markdown file to understand its structure without loading full content.

                                         Modes:
                                         - 'structure' (default): Returns document outline—headings, code blocks, sections, frontmatter
                                         - 'search': Finds text or regex patterns, returns line numbers and context
                                         - 'lines': Returns specific line ranges for quick preview

                                         Use this before TextPatch to:
                                         1. Understand document organization
                                         2. Find the exact line numbers for content you want to modify
                                         3. Locate headings or sections by name
                                         4. Search for specific text to find where changes are needed

                                         Examples:
                                         - Get markdown outline: mode="structure"
                                         - Find all mentions of "config": mode="search", query="config"
                                         - Find regex pattern: mode="search", query="TODO:.*", regex=true
                                         - Preview lines 50-60: mode="lines", query="50-60"
                                         """;

    protected JsonNode Run(string filePath, string mode = "structure", string? query = null, bool regex = false,
        int context = 0)
    {
        var fullPath = ValidateAndResolvePath(filePath);
        var lines = File.ReadAllLines(fullPath);
        var isMarkdown = Path.GetExtension(fullPath).ToLowerInvariant() is ".md" or ".markdown";

        return mode.ToLowerInvariant() switch
        {
            "structure" => InspectStructure(fullPath, lines, isMarkdown),
            "search" => InspectSearch(fullPath, lines,
                query ?? throw new ArgumentException("Query required for search mode"), regex, context, isMarkdown),
            "lines" => InspectLines(fullPath, lines,
                query ?? throw new ArgumentException("Query required for lines mode (e.g., '50-60')")),
            _ => throw new ArgumentException($"Invalid mode '{mode}'. Must be 'structure', 'search', or 'lines'.")
        };
    }

    private JsonNode InspectStructure(string fullPath, string[] lines, bool isMarkdown)
    {
        var result = new JsonObject
        {
            ["filePath"] = fullPath,
            ["totalLines"] = lines.Length,
            ["fileSize"] = FormatFileSize(new FileInfo(fullPath).Length),
            ["format"] = isMarkdown ? "markdown" : "text"
        };

        if (isMarkdown)
        {
            var structure = MarkdownParser.Parse(lines);
            var structureNode = new JsonObject();

            if (structure.Frontmatter is not null)
            {
                structureNode["frontmatter"] = new JsonObject
                {
                    ["startLine"] = structure.Frontmatter.StartLine,
                    ["endLine"] = structure.Frontmatter.EndLine,
                    ["keys"] = new JsonArray(structure.Frontmatter.Keys.Select(k => JsonValue.Create(k)).ToArray())
                };
            }

            var headingsArray = new JsonArray();
            foreach (var h in structure.Headings)
            {
                headingsArray.Add(new JsonObject
                {
                    ["level"] = h.Level,
                    ["text"] = h.Text,
                    ["line"] = h.Line
                });
            }

            structureNode["headings"] = headingsArray;

            var codeBlocksArray = new JsonArray();
            foreach (var cb in structure.CodeBlocks)
            {
                var cbNode = new JsonObject
                {
                    ["startLine"] = cb.StartLine,
                    ["endLine"] = cb.EndLine
                };
                if (cb.Language is not null)
                {
                    cbNode["language"] = cb.Language;
                }

                codeBlocksArray.Add(cbNode);
            }

            structureNode["codeBlocks"] = codeBlocksArray;

            if (structure.Anchors.Count > 0)
            {
                var anchorsArray = new JsonArray();
                foreach (var a in structure.Anchors)
                {
                    anchorsArray.Add(new JsonObject
                    {
                        ["id"] = a.Id,
                        ["line"] = a.Line
                    });
                }

                structureNode["anchors"] = anchorsArray;
            }

            result["structure"] = structureNode;
        }
        else
        {
            var structure = MarkdownParser.ParsePlainText(lines);
            var structureNode = new JsonObject();

            if (structure.Sections.Count > 0)
            {
                var sectionsArray = new JsonArray();
                foreach (var s in structure.Sections)
                {
                    sectionsArray.Add(new JsonObject
                    {
                        ["marker"] = s.Marker,
                        ["line"] = s.Line
                    });
                }

                structureNode["sections"] = sectionsArray;
            }

            if (structure.BlankLineGroups.Count > 0)
            {
                structureNode["blankLineGroups"] = new JsonArray(
                    structure.BlankLineGroups.Select(b => JsonValue.Create(b)).ToArray());
            }

            result["structure"] = structureNode;
        }

        return result;
    }

    private JsonNode InspectSearch(string fullPath, string[] lines, string query, bool isRegex, int contextLines,
        bool isMarkdown)
    {
        var matches = new JsonArray();
        var pattern = isRegex ? new Regex(query, RegexOptions.IgnoreCase) : null;

        MarkdownStructure? mdStructure = null;
        if (isMarkdown)
        {
            mdStructure = MarkdownParser.Parse(lines);
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNumber = i + 1;

            var isMatch = pattern?.IsMatch(line) ?? line.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (!isMatch)
            {
                continue;
            }

            var column = pattern?.Match(line).Index ?? line.IndexOf(query, StringComparison.OrdinalIgnoreCase);

            var matchNode = new JsonObject
            {
                ["line"] = lineNumber,
                ["column"] = column + 1,
                ["text"] = line.Length > 200 ? line[..200] + "..." : line
            };

            if (contextLines > 0)
            {
                var before = new JsonArray();
                var after = new JsonArray();

                for (var j = Math.Max(0, i - contextLines); j < i; j++)
                {
                    before.Add(lines[j].Length > 100 ? lines[j][..100] + "..." : lines[j]);
                }

                for (var j = i + 1; j <= Math.Min(lines.Length - 1, i + contextLines); j++)
                {
                    after.Add(lines[j].Length > 100 ? lines[j][..100] + "..." : lines[j]);
                }

                matchNode["context"] = new JsonObject
                {
                    ["before"] = before,
                    ["after"] = after
                };
            }

            if (mdStructure is not null)
            {
                var nearestHeading = mdStructure.Headings
                    .Where(h => h.Line <= lineNumber)
                    .MaxBy(h => h.Line);

                if (nearestHeading is not null)
                {
                    matchNode["nearestHeading"] = new JsonObject
                    {
                        ["level"] = nearestHeading.Level,
                        ["text"] = nearestHeading.Text,
                        ["line"] = nearestHeading.Line
                    };
                }
            }

            matches.Add(matchNode);
        }

        return new JsonObject
        {
            ["filePath"] = fullPath,
            ["query"] = query,
            ["isRegex"] = isRegex,
            ["matches"] = matches,
            ["totalMatches"] = matches.Count
        };
    }

    private static JsonNode InspectLines(string fullPath, string[] lines, string query)
    {
        var ranges = ParseLineRanges(query, lines.Length);
        var resultLines = new JsonArray();

        foreach (var (start, end) in ranges)
        {
            for (var i = start - 1; i < end && i < lines.Length; i++)
            {
                resultLines.Add(new JsonObject
                {
                    ["number"] = i + 1,
                    ["text"] = lines[i]
                });
            }
        }

        return new JsonObject
        {
            ["filePath"] = fullPath,
            ["query"] = query,
            ["lines"] = resultLines
        };
    }

    private static IEnumerable<(int Start, int End)> ParseLineRanges(string query, int totalLines)
    {
        foreach (var part in query.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Contains('-'))
            {
                var parts = trimmed.Split('-');
                var start = int.Parse(parts[0]);
                var end = parts[1] == "" ? totalLines : int.Parse(parts[1]);
                yield return (Math.Max(1, start), Math.Min(totalLines, end));
            }
            else
            {
                var line = int.Parse(trimmed);
                yield return (line, line);
            }
        }
    }


    private static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes}B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
            _ => $"{bytes / (1024.0 * 1024.0):F1}MB"
        };
    }
}