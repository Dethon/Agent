using Domain.DTOs;
using Domain.DTOs.FileSystem;

namespace Domain.Contracts;

public interface IFileSystemBackend
{
    string FilesystemName { get; }

    Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct);
    Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct);
    Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct);
    Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct);
    Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct);
    Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path, string? directoryPath, string? filePattern,
        int maxResults, int contextLines, VfsTextSearchOutputMode outputMode, CancellationToken ct);
    Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct);
    Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct);
    Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct);

    Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct);

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(string path, CancellationToken ct);

    Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct);
}