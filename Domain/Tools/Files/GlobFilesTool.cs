using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;

namespace Domain.Tools.Files;

public class GlobFilesTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    protected const string Description = """
                                         Searches for files or directories matching a glob pattern relative to the library root.
                                         Supports * (single segment), ** (recursive), and ? (single char).
                                         Use mode 'directories' (default) to explore the library structure first, then 'files' with specific patterns to find content.
                                         In files mode, results are capped at 200—use more specific patterns if truncated.
                                         """;

    private const int FileResultCap = 200;

    protected async Task<JsonNode> Run(string pattern, GlobMode mode, CancellationToken cancellationToken,
        string? basePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        if (pattern.Contains(".."))
        {
            throw new ArgumentException("Pattern must not contain '..' segments", nameof(pattern));
        }

        if (Path.IsPathRooted(pattern))
        {
            if (!pattern.StartsWith(libraryPath.BaseLibraryPath, StringComparison.Ordinal))
            {
                throw new ArgumentException("Absolute pattern must be under the library root", nameof(pattern));
            }

            pattern = Path.GetRelativePath(libraryPath.BaseLibraryPath, pattern);
        }

        var matcherRoot = ResolveMatcherRoot(basePath);

        return mode switch
        {
            GlobMode.Directories => await RunDirectories(matcherRoot, pattern, cancellationToken),
            GlobMode.Files => await RunFiles(matcherRoot, pattern, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Invalid glob mode")
        };
    }

    private string ResolveMatcherRoot(string? basePath)
    {
        if (string.IsNullOrEmpty(basePath))
        {
            return libraryPath.BaseLibraryPath;
        }

        if (basePath.Contains(".."))
        {
            throw new ArgumentException("basePath must not contain '..' segments", nameof(basePath));
        }

        var combined = Path.Combine(libraryPath.BaseLibraryPath, basePath.TrimStart('/'));
        var canonRoot = Path.GetFullPath(combined);
        var canonBase = Path.GetFullPath(libraryPath.BaseLibraryPath);

        if (!canonRoot.StartsWith(canonBase, StringComparison.Ordinal))
        {
            throw new ArgumentException("basePath must resolve under the library root", nameof(basePath));
        }

        return canonRoot;
    }

    private async Task<JsonNode> RunDirectories(string root, string pattern, CancellationToken cancellationToken)
    {
        var result = await client.GlobDirectories(root, pattern, cancellationToken);
        return JsonSerializer.SerializeToNode(result)
               ?? throw new InvalidOperationException("Failed to serialize GlobDirectories result");
    }

    private async Task<JsonNode> RunFiles(string root, string pattern, CancellationToken cancellationToken)
    {
        var result = await client.GlobFiles(root, pattern, cancellationToken);

        if (result.Length <= FileResultCap)
        {
            return JsonSerializer.SerializeToNode(result)
                   ?? throw new InvalidOperationException("Failed to serialize GlobFiles result");
        }

        var truncated = new JsonObject
        {
            ["files"] = JsonSerializer.SerializeToNode(result.Take(FileResultCap).ToArray()),
            ["truncated"] = true,
            ["total"] = result.Length,
            ["message"] = $"Showing {FileResultCap} of {result.Length} matches. Use a more specific pattern to narrow results."
        };
        return truncated;
    }
}