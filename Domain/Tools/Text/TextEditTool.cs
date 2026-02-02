using System.Text.Json.Nodes;

namespace Domain.Tools.Text;

public class TextEditTool(string vaultPath, string[] allowedExtensions) : TextToolBase(vaultPath, allowedExtensions)
{
    protected const string Name = "TextEdit";

    protected const string Description = """
                                         Modifies a text or markdown file using positional targeting or content matching.

                                         Use TextInspect first to find line numbers and headings. To search within files, use TextSearch.

                                         Operations:
                                         - 'replace': Replace targeted content with new content
                                         - 'insert': Insert new content at target location (positional targets only)
                                         - 'delete': Remove targeted content (positional targets only)

                                         Targeting (use ONE key in target JSON):
                                         - lines: { "start": N, "end": M } - Target specific line range
                                         - heading: "## Title" - Target a markdown heading line
                                         - beforeHeading: "## Title" - Position before a heading (for insert)
                                         - appendToSection: "## Title" - Append to end of a markdown section (for insert)
                                         - codeBlock: { "index": N } - Target Nth code block content (0-based)
                                         - text: "exact match" - Find and replace by content match (case-sensitive)

                                         For text targets:
                                         - operation must be 'replace'
                                         - Use occurrence param: 'first' (default), 'last', 'all', or numeric 1-based index
                                         - Case-sensitive matching with case-insensitive suggestions on failure

                                         Examples:
                                         - Replace heading: operation="replace", target={ "heading": "## Old" }, content="## New"
                                         - Insert before heading: operation="insert", target={ "beforeHeading": "## Setup" }, content="## New Section\n"
                                         - Append to section: operation="insert", target={ "appendToSection": "## Setup" }, content="New content\n"
                                         - Delete lines 50-55: operation="delete", target={ "lines": { "start": 50, "end": 55 } }
                                         - Replace code block: operation="replace", target={ "codeBlock": { "index": 0 } }, content="new code..."
                                         - Find and replace text: operation="replace", target={ "text": "old text" }, content="new text"
                                         - Replace all occurrences: operation="replace", target={ "text": "old" }, content="new", occurrence="all"
                                         """;

    protected JsonNode Run(string filePath, string operation, JsonObject target, string? content = null,
        string? occurrence = null, bool preserveIndent = true, string? expectedHash = null)
    {
        var fullPath = ValidateAndResolvePath(filePath);
        var lines = File.ReadAllLines(fullPath);
        ValidateExpectedHash(lines, expectedHash);

        if (target.TryGetPropertyValue("text", out var textNode))
        {
            return RunTextReplace(fullPath, lines, textNode?.GetValue<string>()
                ?? throw new ArgumentException("text target value required"),
                content ?? throw new ArgumentException("Content required for text replace"),
                operation, occurrence ?? "first");
        }

        return RunPositionalEdit(fullPath, lines, operation, target, content, preserveIndent);
    }

    private JsonNode RunTextReplace(string fullPath, string[] originalLines, string oldText, string newText,
        string operation, string occurrence)
    {
        if (operation.ToLowerInvariant() != "replace")
        {
            throw new ArgumentException(
                $"Text target only supports 'replace' operation, not '{operation}'. Use positional targets for insert/delete.");
        }

        var content = File.ReadAllText(fullPath);

        var positions = FindAllOccurrences(content, oldText);

        if (positions.Count == 0)
        {
            var caseSuggestion = FindCaseInsensitiveSuggestion(content, oldText);
            if (caseSuggestion is not null)
            {
                throw new InvalidOperationException(
                    $"Text '{oldText}' not found (case-sensitive). Did you mean '{caseSuggestion}'?");
            }

            throw new InvalidOperationException($"Text '{oldText}' not found in file.");
        }

        var (replacedContent, replacedCount, replacementPosition) =
            ApplyTextReplacement(content, oldText, newText, occurrence, positions);

        var tempPath = fullPath + ".tmp";
        File.WriteAllText(tempPath, replacedContent);
        File.Move(tempPath, fullPath, overwrite: true);

        var (startLine, endLine) = ComputeAffectedLines(content, replacementPosition, oldText.Length);

        var updatedLines = File.ReadAllLines(fullPath);
        var fileHash = ComputeFileHash(updatedLines);

        var result = new JsonObject
        {
            ["status"] = "success",
            ["filePath"] = fullPath,
            ["occurrencesFound"] = positions.Count,
            ["occurrencesReplaced"] = replacedCount,
            ["affectedLines"] = new JsonObject
            {
                ["start"] = startLine,
                ["end"] = endLine
            },
            ["fileHash"] = fileHash
        };

        var beforeText = oldText.Length > 200 ? oldText[..200] + "..." : oldText;
        var afterText = newText.Length > 200 ? newText[..200] + "..." : newText;
        result["preview"] = new JsonObject
        {
            ["before"] = beforeText,
            ["after"] = afterText
        };

        var contextLines = GetTextReplaceContextLines(updatedLines, startLine, endLine);
        result["context"] = new JsonArray(contextLines.Select(l => JsonValue.Create(l)).ToArray());

        if (replacedCount < positions.Count)
        {
            var remaining = positions.Count - replacedCount;
            result["note"] = $"{remaining} other occurrence(s) remain at other locations";
        }

        return result;
    }

    private JsonNode RunPositionalEdit(string fullPath, string[] originalLines, string operation, JsonObject target,
        string? content, bool preserveIndent)
    {
        var linesList = originalLines.ToList();
        var isMarkdown = Path.GetExtension(fullPath).ToLowerInvariant() is ".md" or ".markdown";

        ValidateOperation(operation, content);

        var (startLine, endLine, matchedText) = ResolveTarget(target, linesList, isMarkdown);
        var originalTotalLines = originalLines.Length;
        var originalLineCount = linesList.Count;

        string? previousContent = null;
        if (startLine > 0 && endLine > 0)
        {
            previousContent = string.Join("\n", linesList.Skip(startLine - 1).Take(endLine - startLine + 1));
        }

        var result = operation.ToLowerInvariant() switch
        {
            "replace" => ApplyReplace(linesList, startLine, endLine, matchedText, content!, preserveIndent),
            "insert" => ApplyInsert(linesList, target, startLine, content!, preserveIndent),
            "delete" => ApplyDelete(linesList, startLine, endLine),
            _ => throw new ArgumentException(
                $"Invalid operation '{operation}'. Must be 'replace', 'insert', or 'delete'.")
        };

        var tempPath = fullPath + ".tmp";
        File.WriteAllLines(tempPath, linesList);
        File.Move(tempPath, fullPath, overwrite: true);

        var updatedLines = File.ReadAllLines(fullPath);
        var newEndLine = startLine + (updatedLines.Length - originalTotalLines) + (endLine - startLine);

        var contextBefore = new JsonArray();
        var beforeStart = Math.Max(0, startLine - 3 - 1);
        var beforeEnd = startLine - 1;
        for (var i = beforeStart; i < beforeEnd; i++)
        {
            if (i >= 0 && i < updatedLines.Length)
            {
                contextBefore.Add(updatedLines[i]);
            }
        }

        var contextAfter = new JsonArray();
        var afterStart = newEndLine;
        var afterEnd = Math.Min(updatedLines.Length, afterStart + 3);
        for (var i = afterStart; i < afterEnd; i++)
        {
            if (i >= 0 && i < updatedLines.Length)
            {
                contextAfter.Add(updatedLines[i]);
            }
        }

        result["status"] = "success";
        result["filePath"] = fullPath;
        result["operation"] = operation;
        result["affectedLines"] = new JsonObject
        {
            ["start"] = startLine,
            ["end"] = endLine
        };
        result["linesDelta"] = linesList.Count - originalLineCount;
        result["context"] = new JsonObject
        {
            ["beforeLines"] = contextBefore,
            ["afterLines"] = contextAfter
        };
        result["fileHash"] = ComputeFileHash(updatedLines);

        if (previousContent is not null && previousContent.Length < 500)
        {
            result["preview"] = new JsonObject
            {
                ["before"] = previousContent.Length > 200 ? previousContent[..200] + "..." : previousContent,
                ["after"] = result["newContent"]?.GetValue<string>() ?? ""
            };
        }

        if (linesList.Count != originalLineCount)
        {
            result["note"] = $"File now has {linesList.Count} lines (was {originalLineCount})";
        }

        return result;
    }

    // --- Positional edit helpers (from TextPatchTool) ---

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

        if (target.TryGetPropertyValue("appendToSection", out var appendNode))
        {
            if (!isMarkdown)
            {
                throw new InvalidOperationException("appendToSection targeting only works with markdown files");
            }

            var heading = appendNode?.GetValue<string>() ?? throw new ArgumentException("appendToSection value required");
            var structure = MarkdownParser.Parse(lines.ToArray());
            var headingIndex = FindHeadingIndex(structure, heading);
            var endLine = MarkdownParser.FindHeadingEnd(structure.Headings, headingIndex, lines.Count);
            return (endLine, endLine, null);
        }

        if (target.TryGetPropertyValue("beforeHeading", out var beforeNode))
        {
            if (!isMarkdown)
            {
                throw new InvalidOperationException("Heading targeting only works with markdown files");
            }

            var heading = beforeNode?.GetValue<string>() ?? throw new ArgumentException("beforeHeading value required");
            var (line, _, _) = FindHeadingTarget(lines, heading);
            return (line - 1, line - 1, null);
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

        throw new ArgumentException(
            "Invalid target. Use one of: lines, heading, beforeHeading, appendToSection, codeBlock, text");
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

            if (line.Equals(heading, StringComparison.OrdinalIgnoreCase))
            {
                return (i + 1, i + 1, line);
            }

            var lineNormalized = line.TrimStart('#').Trim();
            if (lineNormalized.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return (i + 1, i + 1, line);
            }
        }

        var structure = MarkdownParser.Parse(lines.ToArray());
        var similar = structure.Headings
            .Where(h => h.Text.Contains(normalized.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .Select(h => $"'{new string('#', h.Level)} {h.Text}' (line {h.Line})");

        throw new InvalidOperationException(
            $"Heading '{heading}' not found. Similar: {string.Join(", ", similar)}. Use TextInspect to list all headings.");
    }

    private static int FindHeadingIndex(MarkdownStructure structure, string heading)
    {
        var normalized = heading.TrimStart('#').Trim();

        for (var i = 0; i < structure.Headings.Count; i++)
        {
            var h = structure.Headings[i];

            var fullHeading = $"{new string('#', h.Level)} {h.Text}";
            if (fullHeading.Equals(heading, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }

            if (h.Text.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

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
        return (block.StartLine + 1, block.EndLine - 1, null);
    }

    private static JsonObject ApplyReplace(List<string> lines, int startLine, int endLine, string? matchedText,
        string content, bool preserveIndent)
    {
        if (matchedText is not null)
        {
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

    // --- Text replace helpers (from TextReplaceTool) ---

    private static List<int> FindAllOccurrences(string content, string searchText)
    {
        var positions = new List<int>();
        var index = 0;

        while ((index = content.IndexOf(searchText, index, StringComparison.Ordinal)) >= 0)
        {
            positions.Add(index);
            index += searchText.Length;
        }

        return positions;
    }

    private static string? FindCaseInsensitiveSuggestion(string content, string searchText)
    {
        var index = content.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? content.Substring(index, searchText.Length) : null;
    }

    private static (string ReplacedContent, int ReplacedCount, int ReplacementPosition) ApplyTextReplacement(
        string content, string oldText, string newText, string occurrence, List<int> positions)
    {
        var occurrenceParam = occurrence.ToLowerInvariant();

        if (occurrenceParam == "all")
        {
            var replaced = content.Replace(oldText, newText);
            return (replaced, positions.Count, positions[0]);
        }

        if (occurrenceParam == "last")
        {
            var position = positions[^1];
            var replaced = content[..position] + newText + content[(position + oldText.Length)..];
            return (replaced, 1, position);
        }

        if (int.TryParse(occurrenceParam, out var nth))
        {
            if (nth < 1 || nth > positions.Count)
            {
                throw new InvalidOperationException(
                    $"Occurrence {nth} requested but only {positions.Count} found");
            }

            var position = positions[nth - 1];
            var replaced = content[..position] + newText + content[(position + oldText.Length)..];
            return (replaced, 1, position);
        }

        var firstPosition = positions[0];
        var replacedFirst = content[..firstPosition] + newText + content[(firstPosition + oldText.Length)..];
        return (replacedFirst, 1, firstPosition);
    }

    private static (int StartLine, int EndLine) ComputeAffectedLines(string content, int position, int oldLength)
    {
        var startLine = content[..position].Count(c => c == '\n') + 1;
        var oldTextContent = content.Substring(position, oldLength);
        var linesInOld = oldTextContent.Count(c => c == '\n');
        var endLine = startLine + linesInOld;
        return (startLine, endLine);
    }

    private static List<string> GetTextReplaceContextLines(string[] lines, int startLine, int endLine)
    {
        const int contextSize = 3;

        var contextStart = Math.Max(0, startLine - 1 - contextSize);
        var contextEnd = Math.Min(lines.Length - 1, endLine - 1 + contextSize);

        var context = new List<string>();
        for (var i = contextStart; i <= contextEnd; i++)
        {
            var lineNum = i + 1;
            var marker = lineNum >= startLine && lineNum <= endLine ? ">" : " ";
            context.Add($"{marker} {lineNum}: {lines[i]}");
        }

        return context;
    }
}
