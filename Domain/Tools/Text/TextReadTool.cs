using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Domain.Tools.Text;

public class TextReadTool(string vaultPath, string[] allowedExtensions)
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

        var fileHash = ComputeFileHash(allLines);

        var result = new JsonObject
        {
            ["filePath"] = fullPath,
            ["content"] = content,
            ["totalLines"] = totalLines,
            ["fileHash"] = fileHash,
            ["truncated"] = truncated
        };

        if (truncated)
        {
            var nextOffset = startIndex + effectiveLimit + 1;
            result["suggestion"] = $"File has more content. Use offset={nextOffset} to continue reading.";
        }

        return result;
    }

    private string ValidateAndResolvePath(string filePath)
    {
        var fullPath = Path.IsPathRooted(filePath)
            ? Path.GetFullPath(filePath)
            : Path.GetFullPath(Path.Combine(vaultPath, filePath));

        if (!fullPath.StartsWith(vaultPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Access denied: path must be within vault directory");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (!allowedExtensions.Contains(ext))
        {
            throw new InvalidOperationException(
                $"File type '{ext}' not allowed. Allowed: {string.Join(", ", allowedExtensions)}");
        }

        return fullPath;
    }

    private static string ComputeFileHash(string[] lines)
    {
        var content = string.Join("\n", lines);
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}