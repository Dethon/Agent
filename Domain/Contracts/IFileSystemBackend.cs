using System.Text.Json.Nodes;

namespace Domain.Contracts;

public interface IFileSystemBackend
{
    string FilesystemName { get; }

    Task<JsonNode> ReadAsync(string path, int? offset, int? limit, CancellationToken ct);
    Task<JsonNode> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct);
    Task<JsonNode> EditAsync(string path, string oldString, string newString, bool replaceAll, CancellationToken ct);
    Task<JsonNode> GlobAsync(string basePath, string pattern, string mode, CancellationToken ct);
    Task<JsonNode> SearchAsync(string query, bool regex, string? path, string? directoryPath, string? filePattern,
        int maxResults, int contextLines, string outputMode, CancellationToken ct);
    Task<JsonNode> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct);
    Task<JsonNode> DeleteAsync(string path, CancellationToken ct);
    Task<JsonNode> ListAsync(string path, CancellationToken ct);
}
