using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;
using Domain.Tools.Text;

namespace Domain.Tools.Files;

// A disk root whose files are text the agent may author: it adds create, edit and text search on
// top of the plain disk operations. Allowed extensions are what make those three possible, so a
// root without them stays a DiskFileSystem and advertises neither.
public class TextDiskFileSystem(
    string filesystemName,
    string mountDescription,
    IFileSystemClient client,
    LibraryPathConfig root,
    string[] allowedExtensions) : DiskFileSystem(filesystemName, mountDescription, client, root)
{
    private readonly TextReadTool _read = new(root.BaseLibraryPath, allowedExtensions);
    private readonly TextCreateTool _create = new(root.BaseLibraryPath, allowedExtensions);
    private readonly TextEditTool _edit = new(root.BaseLibraryPath, allowedExtensions);
    private readonly TextSearchTool _search = new(root.BaseLibraryPath, allowedExtensions);

    public override string DescribeRead =>
        "Reads a text file and returns its content with line numbers. Returns content formatted as "
        + "\"1: first line\\n2: second line\\n...\" with trailing metadata. Large files are truncated "
        + "at 500 lines — use offset and limit for pagination.";

    public override string DescribeCreate =>
        "Creates a new text or markdown file. Use this to create new notes, documentation, or "
        + "configuration files. The file must not already exist unless overwrite is set, and it "
        + "must have an allowed extension. Parent directories are created when createDirectories "
        + "is true (default).";

    public override string DescribeEdit =>
        "Edits a text file by applying a non-empty list of edits in order, atomically. Each edit "
        + "has oldString (exact, case-sensitive), newString and replaceAll (default false). Edits "
        + "are applied sequentially — edit N sees the result of edits 1…N-1. If any edit fails "
        + "(oldString not found, or multiple matches without replaceAll), the file is not written.";

    public override string DescribeSearch =>
        "Searches for text across files, or within a single file. Returns matching files with line "
        + "numbers and context. Scope with directoryPath, or filePath for one file; filter with "
        + "filePattern (e.g. *.md). Supports regex, maxResults, contextLines and outputMode "
        + "(content|filesOnly).";

    public override Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
        Task.FromResult(_read.Run(path, offset, limit));

    public override Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite,
        bool createDirectories, CancellationToken ct) =>
        Task.FromResult(_create.Run(path, content, overwrite, createDirectories));

    public override Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct) =>
        Task.FromResult(_edit.Run(path, edits));

    // The allowed extensions are this root's rule about what its files are, and a blob write is a
    // write like any other — the cross-mount copy streams through the chunk half of it. Ungated, a
    // copy could plant any bytes under any name here, and overwrite an existing note on the way. The
    // gate is per path, not per operation: the mount still advertises fs_blob_write, exactly as it
    // advertises fs_create while refusing a .png.
    public override Task<FsResult<FsBlobWriteResult>> WriteBlobAsync(
        string path, string contentBase64, long offset, bool overwrite, bool createDirectories, CancellationToken ct) =>
        DisallowedExtension(path) is { } refusal
            ? Task.FromResult<FsResult<FsBlobWriteResult>>(new FsResult<FsBlobWriteResult>.Err(refusal))
            : base.WriteBlobAsync(path, contentBase64, offset, overwrite, createDirectories, ct);

    public override Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        DisallowedExtension(path) is { } refusal
            ? throw new FileSystemOperationException(refusal)
            : base.WriteChunksAsync(path, chunks, overwrite, createDirectories, ct);

    // The same refusal the text tools give, in the shape a caller of either write can read: the
    // envelope for the ranged write, the typed exception for the streamed one.
    private ToolErrorResult? DisallowedExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return allowedExtensions.Contains(ext)
            ? null
            : new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.InvalidArgument,
                Message = $"File extension '{ext}' not allowed. Allowed: {string.Join(", ", allowedExtensions)}",
                Retryable = false
            };
    }

    public override Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path,
        string? directoryPath, string? filePattern, int maxResults, int contextLines,
        VfsTextSearchOutputMode outputMode, CancellationToken ct) =>
        Task.FromResult(_search.Run(
            query, regex, path, filePattern, directoryPath ?? "/", maxResults, contextLines, outputMode));
}