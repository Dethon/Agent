using System.Text.Json.Nodes;

namespace Domain.Tools.Text;

public class TextPatchTool(string vaultPath, string[] allowedExtensions) : TextToolBase(vaultPath, allowedExtensions)
{
    protected const string Name = "TextPatch";

    protected const string Description = """
                                         Modifies a text or markdown file with precise targeting.

                                         Operations:
                                         - 'replace': Replace targeted lines with new content
                                         - 'insert': Insert new content at target location
                                         - 'delete': Remove targeted content

                                         Targeting (use ONE):
                                         - lines: { "start": N, "end": M } - Target specific line range
                                         - heading: "## Title" - Target a markdown heading line
                                         - beforeHeading: "## Title" - Position before a heading (for insert)
                                         - codeBlock: { "index": N } - Target Nth code block content

                                         IMPORTANT:
                                         1. Always use TextInspect first to find exact line numbers
                                         2. Prefer heading targeting for markdown—survives other edits
                                         3. Line numbers shift after insert/delete—re-inspect if making multiple edits
                                         4. For text find-and-replace, use TextReplace instead

                                         Examples:
                                         - Replace heading: operation="replace", target={ "heading": "## Old" }, content="## New"
                                         - Insert before heading: operation="insert", target={ "beforeHeading": "## Setup" }, content="## New Section\n"
                                         - Delete lines 50-55: operation="delete", target={ "lines": { "start": 50, "end": 55 } }
                                         - Replace code block: operation="replace", target={ "codeBlock": { "index": 0 } }, content="new code..."
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
            _ => throw new ArgumentException(
                $"Invalid operation '{operation}'. Must be 'replace', 'insert', or 'delete'.")
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
        if (op is not ("replace" or "insert" or "delete"))
        {
            throw new ArgumentException($"Invalid operation '{operation}'. Must be 'replace', 'insert', or 'delete'.");
        }

        if (op is "replace" or "insert" && string.IsNullOrEmpty(content))
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

        if (target.TryGetPropertyValue("heading", out var headingNode))
        {
            if (!isMarkdown)
            {
                throw new InvalidOperationException("Heading targeting only works with markdown files");
            }

            var heading = headingNode?.GetValue<string>() ?? throw new ArgumentException("heading value required");
            return FindHeadingTarget(lines, heading);
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

        // Throw for deprecated targets
        if (target.ContainsKey("text"))
        {
            throw new ArgumentException("The 'text' target is deprecated. Use TextReplace tool for find-and-replace operations.");
        }

        if (target.ContainsKey("pattern"))
        {
            throw new ArgumentException("The 'pattern' target is deprecated. Use TextReplace tool for pattern-based replacements.");
        }

        if (target.ContainsKey("section"))
        {
            throw new ArgumentException("The 'section' target is deprecated and no longer supported.");
        }

        if (target.ContainsKey("afterHeading"))
        {
            throw new ArgumentException("The 'afterHeading' target is deprecated. Use 'beforeHeading' to position before the next heading instead.");
        }

        throw new ArgumentException(
            "Invalid target. Use one of: lines, heading, beforeHeading, codeBlock");
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