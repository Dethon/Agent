using Domain.DTOs.FileSystem;

namespace Domain.Tools.Text;

public class TextReadTool(string vaultPath, string[] allowedExtensions)
    : TextToolBase(vaultPath, allowedExtensions)
{
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

    protected FsResult<FsReadResult> Run(string filePath, int? offset = null, int? limit = null)
    {
        if (!ResolveExistingFile(filePath).TryGetValue(out var fullPath, out var error))
        {
            return new FsResult<FsReadResult>.Err(error);
        }

        var allLines = File.ReadAllLines(fullPath);
        var startIndex = Math.Clamp((offset ?? 1) - 1, 0, allLines.Length);

        var remainingLines = allLines.Skip(startIndex).ToArray();
        var effectiveLimit = Math.Min(limit ?? remainingLines.Length, MaxReturnLines);
        var selectedLines = remainingLines.Take(effectiveLimit).ToArray();
        var truncated = remainingLines.Length > effectiveLimit;

        var content = string.Join("\n", selectedLines.Select((line, i) => $"{startIndex + i + 1}: {line}"));

        return new FsResult<FsReadResult>.Ok(new FsReadResult
        {
            FilePath = fullPath,
            Content = content,
            TotalLines = allLines.Length,
            Truncated = truncated,
            Suggestion = truncated
                ? $"File has more content. Use offset={startIndex + effectiveLimit + 1} to continue reading."
                : null
        });
    }
}