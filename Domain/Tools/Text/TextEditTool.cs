using System.Text.Json.Nodes;

namespace Domain.Tools.Text;

public class TextEditTool(string vaultPath, string[] allowedExtensions)
    : TextToolBase(vaultPath, allowedExtensions)
{
    protected const string Name = "TextEdit";

    protected const string Description = """
                                         Edits a text file by replacing exact string matches.

                                         Parameters:
                                         - filePath: Path to file (absolute or relative to vault)
                                         - oldString: Exact text to find (case-sensitive)
                                         - newString: Replacement text
                                         - replaceAll: Replace all occurrences (default: false)

                                         When replaceAll is false, oldString must appear exactly once.
                                         If multiple occurrences are found, the tool fails with the count — provide more surrounding context in oldString to disambiguate.

                                         Insert: include surrounding context in oldString, add new lines in newString.
                                         Delete: include content in oldString, omit it from newString.
                                         """;

    protected JsonNode Run(string filePath, string oldString, string newString, bool replaceAll = false)
    {
        var fullPath = ValidateAndResolvePath(filePath);
        var content = File.ReadAllText(fullPath);

        var positions = FindAllOccurrences(content, oldString);

        if (positions.Count == 0)
        {
            var suggestion = FindCaseInsensitiveSuggestion(content, oldString);
            if (suggestion is not null)
            {
                throw new InvalidOperationException(
                    $"Text '{Truncate(oldString, 100)}' not found (case-sensitive). Did you mean '{Truncate(suggestion, 100)}'?");
            }

            throw new InvalidOperationException($"Text '{Truncate(oldString, 100)}' not found in file.");
        }

        if (!replaceAll && positions.Count > 1)
        {
            throw new InvalidOperationException(
                $"Found {positions.Count} occurrences of the specified text. Provide more surrounding context in oldString to disambiguate, or set replaceAll=true.");
        }

        var replacedContent = replaceAll
            ? content.Replace(oldString, newString, StringComparison.Ordinal)
            : ReplaceFirst(content, oldString, newString, positions[0]);

        var replacedCount = replaceAll ? positions.Count : 1;

        var tempPath = fullPath + ".tmp";
        File.WriteAllText(tempPath, replacedContent);
        File.Move(tempPath, fullPath, overwrite: true);

        var (startLine, endLine) = ComputeAffectedLines(content, positions[0], oldString.Length);
        var updatedLines = File.ReadAllLines(fullPath);
        var fileHash = ComputeFileHash(updatedLines);

        return new JsonObject
        {
            ["status"] = "success",
            ["filePath"] = fullPath,
            ["occurrencesReplaced"] = replacedCount,
            ["affectedLines"] = new JsonObject
            {
                ["start"] = startLine,
                ["end"] = endLine
            },
            ["fileHash"] = fileHash
        };
    }

    private static string ReplaceFirst(string content, string oldString, string newString, int position)
    {
        return content[..position] + newString + content[(position + oldString.Length)..];
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
        return index >= 0 ? content.Substring(index, searchText.Length) : null;
    }

    private static (int StartLine, int EndLine) ComputeAffectedLines(string content, int position, int oldLength)
    {
        var startLine = content[..position].Count(c => c == '\n') + 1;
        var oldTextContent = content.Substring(position, oldLength);
        var linesInOld = oldTextContent.Count(c => c == '\n');
        return (startLine, startLine + linesInOld);
    }

    private static string Truncate(string text, int maxLength)
    {
        return text.Length > maxLength ? text[..maxLength] + "..." : text;
    }
}
