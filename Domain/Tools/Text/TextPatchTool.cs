using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Domain.Tools.Text;

public class TextPatchTool(string vaultPath, string[] allowedExtensions) : TextToolBase(vaultPath, allowedExtensions)
{
    protected const string Name = "TextPatch";

    protected const string Description = """
                                         Modifies a text or markdown file with precise targeting.

                                         Operations:
                                         - 'replace': Replace targeted text/lines with new content
                                         - 'insert': Insert new content at target location
                                         - 'delete': Remove targeted content
                                         - 'replaceLines': Replace a range of lines (handles line count changes)

                                         Targeting (use ONE):
                                         - lines: { "start": N, "end": M } - Target specific line range
                                         - text: "exact text" - Find and target literal text (first occurrence)
                                         - pattern: "regex" - Find and target regex match (first occurrence)
                                         - heading: "## Title" - Target a markdown heading line
                                         - afterHeading: "## Title" - Position after a heading (for insert)
                                         - beforeHeading: "## Title" - Position before a heading (for insert)
                                         - codeBlock: { "index": N } - Target Nth code block content
                                         - section: "[name]" - Target INI section

                                         IMPORTANT:
                                         1. Always use TextInspect first to find exact line numbers and text
                                         2. Prefer heading/section targeting for markdown—survives other edits
                                         3. Use text/pattern targeting for inline changes
                                         4. Line numbers shift after insert/delete—re-inspect if making multiple edits

                                         Examples:
                                         - Replace heading: operation="replace", target={ "heading": "## Old" }, content="## New"
                                         - Insert after heading: operation="insert", target={ "afterHeading": "## Intro" }, content="\nNew paragraph..."
                                         - Delete lines 50-55: operation="delete", target={ "lines": { "start": 50, "end": 55 } }
                                         - Replace code block: operation="replace", target={ "codeBlock": { "index": 0 } }, content="new code..."
                                         - Find and replace: operation="replace", target={ "text": "v1.0.0" }, content="v2.0.0"
                                         """;

    protected JsonNode Run(string filePath, string operation, JsonObject target, string? content = null,
        bool preserveIndent = true)
    {
        var fullPath = ValidateAndResolvePath(filePath);
        var lines = File.ReadAllLines(fullPath).ToList();
        var isMarkdown = Path.GetExtension(fullPath).ToLowerInvariant() is ".md" or ".markdown";

        ValidateOperation(operation, content);

        var (startLine, endLine, matchedText) = ResolveTarget(target, lines, isMarkdown);
        var originalLineCount = lines.Count;

        string? previousContent = null;
        if (startLine > 0 && endLine > 0)
        {
            previousContent = string.Join("\n", lines.Skip(startLine - 1).Take(endLine - startLine + 1));
        }

        var result = operation.ToLowerInvariant() switch
        {
            "replace" => ApplyReplace(lines, startLine, endLine, matchedText, content!, preserveIndent),
            "insert" => ApplyInsert(lines, target, startLine, content!, preserveIndent),
            "delete" => ApplyDelete(lines, startLine, endLine),
            "replacelines" => ApplyReplaceLines(lines, startLine, endLine, content!),
            _ => throw new ArgumentException(
                $"Invalid operation '{operation}'. Must be 'replace', 'insert', 'delete', or 'replaceLines'.")
        };

        // Write atomically
        var tempPath = fullPath + ".tmp";
        File.WriteAllLines(tempPath, lines);
        File.Move(tempPath, fullPath, overwrite: true);

        result["status"] = "success";
        result["filePath"] = fullPath;
        result["operation"] = operation;
        result["affectedLines"] = new JsonObject
        {
            ["start"] = startLine,
            ["end"] = endLine
        };
        result["linesDelta"] = lines.Count - originalLineCount;

        if (previousContent is not null && previousContent.Length < 500)
        {
            result["preview"] = new JsonObject
            {
                ["before"] = previousContent.Length > 200 ? previousContent[..200] + "..." : previousContent,
                ["after"] = result["newContent"]?.GetValue<string>() ?? ""
            };
        }

        if (lines.Count != originalLineCount)
        {
            result["note"] = $"File now has {lines.Count} lines (was {originalLineCount})";
        }

        return result;
    }

    private static void ValidateOperation(string operation, string? content)
    {
        var op = operation.ToLowerInvariant();
        if (op is "replace" or "insert" or "replacelines" && string.IsNullOrEmpty(content))
        {
            throw new ArgumentException($"Content required for '{operation}' operation");
        }
    }

    private (int StartLine, int EndLine, string? MatchedText) ResolveTarget(JsonObject target, List<string> lines,
        bool isMarkdown)
    {
        if (target.TryGetPropertyValue("lines", out var linesNode) && linesNode is JsonObject linesObj)
        {
            var start = linesObj["start"]?.GetValue<int>() ?? throw new ArgumentException("lines.start required");
            var end = linesObj["end"]?.GetValue<int>() ?? start;
            ValidateLineRange(start, end, lines.Count);
            return (start, end, null);
        }

        if (target.TryGetPropertyValue("text", out var textNode))
        {
            var searchText = textNode?.GetValue<string>() ?? throw new ArgumentException("text value required");
            return FindTextTarget(lines, searchText);
        }

        if (target.TryGetPropertyValue("pattern", out var patternNode))
        {
            var pattern = patternNode?.GetValue<string>() ?? throw new ArgumentException("pattern value required");
            var flags = target["flags"]?.GetValue<string>();
            return FindPatternTarget(lines, pattern, flags);
        }

        if (target.TryGetPropertyValue("heading", out var headingNode))
        {
            if (!isMarkdown)
            {
                throw new InvalidOperationException("Heading targeting only works with markdown files");
            }

            var heading = headingNode?.GetValue<string>() ?? throw new ArgumentException("heading value required");
            return FindHeadingTarget(lines, heading);
        }

        if (target.TryGetPropertyValue("afterHeading", out var afterNode))
        {
            if (!isMarkdown)
            {
                throw new InvalidOperationException("Heading targeting only works with markdown files");
            }

            var heading = afterNode?.GetValue<string>() ?? throw new ArgumentException("afterHeading value required");
            var (line, _, _) = FindHeadingTarget(lines, heading);
            return (line, line, null); // Insert point is after this line
        }

        if (target.TryGetPropertyValue("beforeHeading", out var beforeNode))
        {
            if (!isMarkdown)
            {
                throw new InvalidOperationException("Heading targeting only works with markdown files");
            }

            var heading = beforeNode?.GetValue<string>() ?? throw new ArgumentException("beforeHeading value required");
            var (line, _, _) = FindHeadingTarget(lines, heading);
            return (line - 1, line - 1, null); // Insert point is before this line
        }

        if (target.TryGetPropertyValue("codeBlock", out var codeBlockNode) && codeBlockNode is JsonObject codeBlockObj)
        {
            if (!isMarkdown)
            {
                throw new InvalidOperationException("Code block targeting only works with markdown files");
            }

            var index = codeBlockObj["index"]?.GetValue<int>() ??
                        throw new ArgumentException("codeBlock.index required");
            return FindCodeBlockTarget(lines, index);
        }

        if (target.TryGetPropertyValue("section", out var sectionNode))
        {
            var marker = sectionNode?.GetValue<string>() ?? throw new ArgumentException("section value required");
            return FindSectionTarget(lines, marker);
        }

        throw new ArgumentException(
            "Invalid target. Use one of: lines, text, pattern, heading, afterHeading, beforeHeading, codeBlock, section");
    }

    private static void ValidateLineRange(int start, int end, int totalLines)
    {
        if (start < 1 || start > totalLines)
        {
            throw new ArgumentException($"Start line {start} out of range. File has {totalLines} lines.");
        }

        if (end < start || end > totalLines)
        {
            throw new ArgumentException($"End line {end} out of range. Must be >= {start} and <= {totalLines}.");
        }
    }

    private static (int, int, string?) FindTextTarget(List<string> lines, string searchText)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var col = lines[i].IndexOf(searchText, StringComparison.Ordinal);
            if (col >= 0)
            {
                return (i + 1, i + 1, searchText);
            }
        }

        // Try case-insensitive as fallback and suggest
        for (var i = 0; i < lines.Count; i++)
        {
            var col = lines[i].IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
            if (col >= 0)
            {
                var actual = lines[i].Substring(col, searchText.Length);
                throw new InvalidOperationException(
                    $"Exact text '{searchText}' not found. Did you mean '{actual}' (case-insensitive match at line {i + 1})?");
            }
        }

        throw new InvalidOperationException(
            $"Text '{searchText}' not found in file. Use TextInspect with search mode to locate text.");
    }

    private static (int, int, string?) FindPatternTarget(List<string> lines, string pattern, string? flags)
    {
        var options = RegexOptions.None;
        if (flags?.Contains('i') == true)
        {
            options |= RegexOptions.IgnoreCase;
        }

        if (flags?.Contains('m') == true)
        {
            options |= RegexOptions.Multiline;
        }

        var regex = new Regex(pattern, options);

        for (var i = 0; i < lines.Count; i++)
        {
            var match = regex.Match(lines[i]);
            if (match.Success)
            {
                return (i + 1, i + 1, match.Value);
            }
        }

        throw new InvalidOperationException(
            $"Pattern '{pattern}' not found in file. Use TextInspect with search mode to test patterns.");
    }

    private static (int, int, string?) FindHeadingTarget(List<string> lines, string heading)
    {
        var normalized = heading.TrimStart('#').Trim();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!line.StartsWith('#'))
            {
                continue;
            }

            // Check exact match first
            if (line.Equals(heading, StringComparison.OrdinalIgnoreCase))
            {
                return (i + 1, i + 1, line);
            }

            // Check normalized match
            var lineNormalized = line.TrimStart('#').Trim();
            if (lineNormalized.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return (i + 1, i + 1, line);
            }
        }

        // Find similar headings
        var structure = MarkdownParser.Parse(lines.ToArray());
        var similar = structure.Headings
            .Where(h => h.Text.Contains(normalized.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .Select(h => $"'{new string('#', h.Level)} {h.Text}' (line {h.Line})");

        throw new InvalidOperationException(
            $"Heading '{heading}' not found. Similar: {string.Join(", ", similar)}. Use TextInspect to list all headings.");
    }

    private static (int, int, string?) FindCodeBlockTarget(List<string> lines, int index)
    {
        var structure = MarkdownParser.Parse(lines.ToArray());

        if (index < 0 || index >= structure.CodeBlocks.Count)
        {
            throw new InvalidOperationException(
                $"Code block index {index} out of range. File has {structure.CodeBlocks.Count} code blocks.");
        }

        var block = structure.CodeBlocks[index];
        // Return the content lines (excluding the ``` markers)
        return (block.StartLine + 1, block.EndLine - 1, null);
    }

    private static (int, int, string?) FindSectionTarget(List<string> lines, string marker)
    {
        var structure = MarkdownParser.ParsePlainText(lines.ToArray());

        var sectionIndex = structure.Sections
            .Select((s, i) => (s, i))
            .FirstOrDefault(x => x.s.Marker.Equals(marker, StringComparison.OrdinalIgnoreCase));

        if (sectionIndex.s is null)
        {
            var available = structure.Sections.Select(s => s.Marker).Take(10);
            throw new InvalidOperationException(
                $"Section '{marker}' not found. Available: {string.Join(", ", available)}");
        }

        var startLine = sectionIndex.s.Line;
        var endLine = MarkdownParser.FindSectionEnd(structure.Sections, sectionIndex.i, lines.Count);

        return (startLine, endLine, null);
    }

    private static JsonObject ApplyReplace(List<string> lines, int startLine, int endLine, string? matchedText,
        string content, bool preserveIndent)
    {
        if (matchedText is not null)
        {
            // Inline text replacement
            var lineIndex = startLine - 1;
            var line = lines[lineIndex];
            var newLine = line.Replace(matchedText, content);
            lines[lineIndex] = newLine;

            return new JsonObject
            {
                ["linesChanged"] = 1,
                ["newContent"] = newLine.Length > 200 ? newLine[..200] + "..." : newLine
            };
        }

        // Full line replacement
        var indent = preserveIndent ? GetIndent(lines[startLine - 1]) : "";
        var newLines = content.Split('\n').Select(l => indent + l.TrimStart()).ToList();

        lines.RemoveRange(startLine - 1, endLine - startLine + 1);
        lines.InsertRange(startLine - 1, newLines);

        return new JsonObject
        {
            ["linesChanged"] = endLine - startLine + 1,
            ["newContent"] = content.Length > 200 ? content[..200] + "..." : content
        };
    }

    private static JsonObject ApplyInsert(List<string> lines, JsonObject target, int insertAfterLine, string content,
        bool preserveIndent)
    {
        var indent = "";
        if (preserveIndent && insertAfterLine > 0 && insertAfterLine <= lines.Count)
        {
            indent = GetIndent(lines[insertAfterLine - 1]);
        }

        var newLines = content.Split('\n').Select(l => indent + l.TrimStart()).ToList();

        // For beforeHeading, insert before the line
        if (target.ContainsKey("beforeHeading"))
        {
            lines.InsertRange(insertAfterLine, newLines);
        }
        else
        {
            lines.InsertRange(insertAfterLine, newLines);
        }

        return new JsonObject
        {
            ["linesChanged"] = 0,
            ["linesInserted"] = newLines.Count,
            ["newContent"] = content.Length > 200 ? content[..200] + "..." : content
        };
    }

    private static JsonObject ApplyDelete(List<string> lines, int startLine, int endLine)
    {
        var deletedContent = string.Join("\n", lines.Skip(startLine - 1).Take(endLine - startLine + 1));
        lines.RemoveRange(startLine - 1, endLine - startLine + 1);

        return new JsonObject
        {
            ["linesDeleted"] = endLine - startLine + 1,
            ["deletedContent"] = deletedContent.Length > 200 ? deletedContent[..200] + "..." : deletedContent
        };
    }

    private static JsonObject ApplyReplaceLines(List<string> lines, int startLine, int endLine, string content)
    {
        var newLines = content.Split('\n').ToList();
        lines.RemoveRange(startLine - 1, endLine - startLine + 1);
        lines.InsertRange(startLine - 1, newLines);

        return new JsonObject
        {
            ["linesChanged"] = endLine - startLine + 1,
            ["newContent"] = content.Length > 200 ? content[..200] + "..." : content
        };
    }

    private static string GetIndent(string line)
    {
        var indent = 0;
        foreach (var c in line)
        {
            if (c == ' ' || c == '\t')
            {
                indent++;
            }
            else
            {
                break;
            }
        }

        return line[..indent];
    }

}