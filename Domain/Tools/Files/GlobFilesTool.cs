using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;

namespace Domain.Tools.Files;

public class GlobFilesTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    protected const string Name = "GlobFiles";

    protected const string Description = """
                                         Searches for files matching a glob pattern relative to the library root.
                                         Supports * (single segment), ** (recursive), and ? (single char).
                                         Returns absolute file paths. Examples: **/*.pdf, books/*, src/**/*.cs.
                                         """;

    protected async Task<JsonNode> Run(string pattern, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        if (pattern.Contains(".."))
        {
            throw new ArgumentException("Pattern must not contain '..' segments", nameof(pattern));
        }

        var result = await client.GlobFiles(libraryPath.BaseLibraryPath, pattern, cancellationToken);
        return JsonSerializer.SerializeToNode(result)
               ?? throw new InvalidOperationException("Failed to serialize GlobFiles result");
    }
}
