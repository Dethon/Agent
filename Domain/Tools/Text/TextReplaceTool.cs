using System.Text.Json.Nodes;

namespace Domain.Tools.Text;

public class TextReplaceTool(string vaultPath, string[] allowedExtensions) : TextToolBase(vaultPath, allowedExtensions)
{
    protected const string Name = "TextReplace";

    protected const string Description = """
                                         Performs search-and-replace operations in text files.

                                         Supports:
                                         - Single occurrence replacement (first/last/Nth)
                                         - Replace all occurrences
                                         - Multiline text replacement
                                         - Case-sensitive matching with case-insensitive suggestions
                                         - Optional file hash validation for conflict detection

                                         Parameters:
                                         - filePath: Path to the file (absolute or relative to vault)
                                         - oldText: Exact text to find (case-sensitive)
                                         - newText: Replacement text
                                         - occurrence: "first" (default), "last", "all", or numeric (1-based index)
                                         - expectedHash: Optional 16-char hash for validation

                                         Returns:
                                         - Status, file path, occurrences found/replaced
                                         - Preview of change (before/after, truncated at 200 chars)
                                         - Context lines (3 before/after affected area)
                                         - Affected line range
                                         - File hash for future validation
                                         - Note if other occurrences remain after replacement

                                         Examples:
                                         - Replace first: oldText="v1.0", newText="v2.0"
                                         - Replace last: oldText="TODO", newText="DONE", occurrence="last"
                                         - Replace all: oldText="old", newText="new", occurrence="all"
                                         - Replace 3rd: oldText="item", newText="ITEM", occurrence="3"

                                         For structural edits by heading, line range, or code block position, use TextPatch instead.
                                         """;

    protected JsonNode Run(string filePath, string oldText, string newText, string occurrence = "first",
        string? expectedHash = null)
    {
        var fullPath = ValidateAndResolvePath(filePath);

        // Read file as lines for hash validation
        var lines = File.ReadAllLines(fullPath);
        ValidateExpectedHash(lines, expectedHash);

        // Read file as single string for replacement
        var content = File.ReadAllText(fullPath);

        // Find all occurrences
        var positions = FindAllOccurrences(content, oldText);

        if (positions.Count == 0)
        {
            // Try case-insensitive search and suggest
            var caseSuggestion = FindCaseInsensitiveSuggestion(content, oldText);
            if (caseSuggestion is not null)
            {
                throw new InvalidOperationException(
                    $"Text '{oldText}' not found (case-sensitive). Did you mean '{caseSuggestion}'?");
            }

            throw new InvalidOperationException($"Text '{oldText}' not found in file.");
        }

        // Determine which occurrence(s) to replace
        var (replacedContent, replacedCount, replacementPosition) =
            ApplyReplacement(content, oldText, newText, occurrence, positions);

        // Write atomically
        var tempPath = fullPath + ".tmp";
        File.WriteAllText(tempPath, replacedContent);
        File.Move(tempPath, fullPath, overwrite: true);

        // Compute affected line range
        var (startLine, endLine) = ComputeAffectedLines(content, replacementPosition, oldText.Length);

        // Read updated lines for hash
        var updatedLines = File.ReadAllLines(fullPath);
        var fileHash = ComputeFileHash(updatedLines);

        // Build response
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

        // Add preview
        var beforeText = oldText.Length > 200 ? oldText[..200] + "..." : oldText;
        var afterText = newText.Length > 200 ? newText[..200] + "..." : newText;
        result["preview"] = new JsonObject
        {
            ["before"] = beforeText,
            ["after"] = afterText
        };

        // Add context lines
        var contextLines = GetContextLines(updatedLines, startLine, endLine);
        result["context"] = new JsonArray(contextLines.Select(l => JsonValue.Create(l)).ToArray());

        // Add note if other occurrences remain
        if (replacedCount < positions.Count)
        {
            var remaining = positions.Count - replacedCount;
            result["note"] = $"{remaining} other occurrence(s) remain at other locations";
        }

        return result;
    }

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
        if (index >= 0)
        {
            return content.Substring(index, searchText.Length);
        }

        return null;
    }

    private static (string ReplacedContent, int ReplacedCount, int ReplacementPosition) ApplyReplacement(
        string content, string oldText, string newText, string occurrence, List<int> positions)
    {
        var occurrenceParam = occurrence.ToLowerInvariant();

        if (occurrenceParam == "all")
        {
            // Replace all occurrences
            var replaced = content.Replace(oldText, newText);
            return (replaced, positions.Count, positions[0]);
        }

        if (occurrenceParam == "last")
        {
            // Replace last occurrence
            var position = positions[^1];
            var replaced = content[..position] + newText + content[(position + oldText.Length)..];
            return (replaced, 1, position);
        }

        if (int.TryParse(occurrenceParam, out var nth))
        {
            // Replace Nth occurrence (1-based)
            if (nth < 1 || nth > positions.Count)
            {
                throw new InvalidOperationException(
                    $"Occurrence {nth} requested but only {positions.Count} found");
            }

            var position = positions[nth - 1];
            var replaced = content[..position] + newText + content[(position + oldText.Length)..];
            return (replaced, 1, position);
        }

        // Default: replace first occurrence
        var firstPosition = positions[0];
        var replacedFirst = content[..firstPosition] + newText + content[(firstPosition + oldText.Length)..];
        return (replacedFirst, 1, firstPosition);
    }

    private static (int StartLine, int EndLine) ComputeAffectedLines(string content, int position, int oldLength)
    {
        // Count lines up to the replacement position
        var startLine = content[..position].Count(c => c == '\n') + 1;

        // Count lines in the old text
        var oldTextContent = content.Substring(position, oldLength);
        var linesInOld = oldTextContent.Count(c => c == '\n');

        var endLine = startLine + linesInOld;

        return (startLine, endLine);
    }

    private static List<string> GetContextLines(string[] lines, int startLine, int endLine)
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