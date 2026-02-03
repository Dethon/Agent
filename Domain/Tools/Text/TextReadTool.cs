using System.Text.Json.Nodes;

namespace Domain.Tools.Text;

public class TextReadTool(string vaultPath, string[] allowedExtensions)
    : TextToolBase(vaultPath, allowedExtensions)
{
    protected const string Name = "TextRead";

    protected const string Description = """
                                         Reads a text file and returns its content with line numbers.

                                         Returns content formatted as "1: first line\n2: second line\n..." with trailing metadata.
                                         Large files are truncated at 500 lines — use offset and limit for pagination.

                                         Parameters:
                                         - filePath: Path to file (absolute or relative to vault)
                                         - offset: Start from this line number (1-based, default: 1)
                                         - limit: Max lines to return (default: all remaining lines)
                                         """;

    private const int MaxReturnLines = 500;

    protected JsonNode Run(string filePath, int? offset = null, int? limit = null)
    {
        var fullPath = ValidateAndResolvePath(filePath);
        var allLines = File.ReadAllLines(fullPath);
        var totalLines = allLines.Length;

        var startIndex = (offset ?? 1) - 1;
        if (startIndex < 0)
        {
            startIndex = 0;
        }

        if (startIndex > allLines.Length)
        {
            startIndex = allLines.Length;
        }

        var remainingLines = allLines.Skip(startIndex).ToArray();
        var requestedLimit = limit ?? remainingLines.Length;
        var effectiveLimit = Math.Min(requestedLimit, MaxReturnLines);
        var selectedLines = remainingLines.Take(effectiveLimit).ToArray();
        var truncated = remainingLines.Length > effectiveLimit;

        var numberedLines = selectedLines
            .Select((line, i) => $"{startIndex + i + 1}: {line}");
        var content = string.Join("\n", numberedLines);

        var result = new JsonObject
        {
            ["filePath"] = fullPath,
            ["content"] = content,
            ["totalLines"] = totalLines,
            ["truncated"] = truncated
        };

        if (truncated)
        {
            var nextOffset = startIndex + effectiveLimit + 1;
            result["suggestion"] = $"File has more content. Use offset={nextOffset} to continue reading.";
        }

        return result;
    }
}