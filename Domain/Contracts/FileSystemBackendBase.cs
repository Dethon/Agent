using System.Text.RegularExpressions;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.FileSystem;

namespace Domain.Contracts;

// Every filesystem backend starts here: the twelve operations already answered as unsupported, plus
// the pieces each backend used to copy — the error envelopes, the glob prologue, a guarded search
// regex and the search template. A backend overrides only what it can really do, and that override
// is the single declaration of its capability: the MCP registrar reflects over it to decide which
// fs_* tools the server advertises, so a mount cannot promise an operation it does not implement.
public abstract class FileSystemBackendBase : IFileSystemBackend
{
    public abstract string FilesystemName { get; }

    // A caller-supplied pattern can be pathological, so every search matches under a bounded
    // timeout. Overridable because a test needs to trip it without waiting a real second.
    protected virtual TimeSpan SearchMatchTimeout => TimeSpan.FromSeconds(1);

    public virtual Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsReadResult>(VfsTextReadTool.Name));

    public virtual Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsInfoResult>(VfsFileInfoTool.Name));

    public virtual Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite,
        bool createDirectories, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsCreateResult>(VfsTextCreateTool.Name));

    public virtual Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsEditResult>(VfsTextEditTool.Name));

    public virtual Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsGlobResult>(VfsGlobFilesTool.Name));

    public virtual Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path,
        string? directoryPath, string? filePattern, int maxResults, int contextLines,
        VfsTextSearchOutputMode outputMode, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsSearchResult>(VfsTextSearchTool.Name));

    public virtual Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsMoveResult>(VfsMoveTool.Name));

    public virtual Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsRemoveResult>(VfsRemoveTool.Name));

    public virtual Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsExecResult>(VfsExecTool.Name));

    public virtual Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsCopyResult>(VfsCopyTool.Name));

    // The two byte-streaming operations are shaped as streams rather than envelopes, so an
    // unimplemented one throws instead. Nothing calls them on a backend that does not override
    // them: the registrar only advertises fs_blob_read/fs_blob_write where the override exists.
    public virtual IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(string path, CancellationToken ct) =>
        throw new NotSupportedException(UnsupportedMessage(BlobReadOperation));

    public virtual Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        throw new NotSupportedException(UnsupportedMessage(BlobWriteOperation));

    public const string BlobReadOperation = "blob_read";
    public const string BlobWriteOperation = "blob_write";

    // The two byte-streaming operations as the wire sees them: ranged rather than streamed,
    // because one MCP call carries one chunk. These are not a thirteenth and fourteenth operation
    // — capability still comes from the chunk methods above — only the shape the tools take. The
    // defaults drive the stream, and a backend with real random access overrides them.
    public virtual async Task<FsResult<FsBlobReadResult>> ReadBlobAsync(
        string path, long offset, int length, CancellationToken ct)
    {
        var all = new List<byte>();
        await foreach (var chunk in ReadChunksAsync(path, ct))
        {
            all.AddRange(chunk.ToArray());
        }

        var from = (int)Math.Clamp(offset, 0, all.Count);
        var take = Math.Clamp(length, 0, all.Count - from);
        return new FsResult<FsBlobReadResult>.Ok(new FsBlobReadResult
        {
            ContentBase64 = Convert.ToBase64String(all.GetRange(from, take).ToArray()),
            Eof = from + take >= all.Count,
            TotalBytes = all.Count
        });
    }

    public virtual async Task<FsResult<FsBlobWriteResult>> WriteBlobAsync(
        string path, string contentBase64, long offset, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(contentBase64);
        }
        catch (FormatException ex)
        {
            return Invalid<FsBlobWriteResult>($"contentBase64 is not valid base64: {ex.Message}");
        }

        var written = await WriteChunksAsync(path, One(bytes), overwrite, createDirectories, ct);
        return new FsResult<FsBlobWriteResult>.Ok(new FsBlobWriteResult
        {
            Path = path,
            BytesWritten = bytes.Length,
            TotalBytes = written
        });
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> One(ReadOnlyMemory<byte> bytes)
    {
        await Task.CompletedTask;
        yield return bytes;
    }

    public virtual string DescribeRead => "Read a text file from this filesystem.";
    public virtual string DescribeInfo => "Get metadata for a path: exists, isDirectory, size, lastModified.";
    public virtual string DescribeCreate => "Create a file in this filesystem.";
    public virtual string DescribeEdit => "Edit a text file by applying an ordered list of replacements.";

    public virtual string DescribeGlob =>
        "List entries matching a glob pattern under basePath. `*` matches one path segment, `**` "
        + "recurses, `?` one char, `{a,b}` brace alternation. A trailing slash (e.g. `*/`) lists "
        + "directories only; otherwise files and directories both match, with directory results "
        + "marked by a trailing slash.";

    public virtual string DescribeSearch =>
        "Search file content across this filesystem. Scope with directoryPath or path (a single "
        + "file); omit both to search everything. Supports regex, filePattern, maxResults, "
        + "contextLines, and outputMode (content|filesOnly).";

    public virtual string DescribeMove => "Move or rename a file or directory within this filesystem.";
    public virtual string DescribeDelete => "Delete a file or directory from this filesystem.";
    public virtual string DescribeExec => "Run a command in this filesystem.";
    public virtual string DescribeCopy => "Copy a file or directory within this filesystem.";

    public virtual string DescribeBlobRead =>
        "Read a chunk of a file's raw bytes as base64. Returns { contentBase64, eof, totalBytes }.";

    public virtual string DescribeBlobWrite =>
        "Write a chunk of raw bytes (base64) at the given offset. offset=0 creates or truncates.";

    protected FsResult<T> Unsupported<T>(string operation) where T : class =>
        FsError.Fail<T>(ToolError.Codes.UnsupportedOperation, UnsupportedMessage(operation));

    protected static FsResult<T> NotFound<T>(string path) where T : class => FsError.NotFound<T>(path);

    protected static FsResult<T> Invalid<T>(string message) where T : class => FsError.Invalid<T>(message);

    protected static FsResult<T> ReadOnly<T>(string path) where T : class => FsError.ReadOnly<T>(path);

    protected static FsResult<T> Fail<T>(string code, string message, bool retryable = false, string? hint = null)
        where T : class => FsError.Fail<T>(code, message, retryable, hint);

    // Applies the two glob conventions the tool advertises to the model and every mount must
    // honour: basePath scopes the pattern, and a trailing slash asks for directories only.
    // Candidates handed to the returned matcher are mount-root-relative, without a leading slash.
    protected static (bool DirsOnly, Func<string, bool> Matches) GlobPrologue(string? basePath, string pattern)
    {
        var prefix = string.IsNullOrEmpty(basePath?.Trim('/')) ? string.Empty : basePath.Trim('/') + "/";
        var dirsOnly = pattern.EndsWith('/');
        var effectivePattern = dirsOnly ? pattern.TrimEnd('/') : pattern;
        return (dirsOnly, GlobRegex.CompileMatcher(prefix + effectivePattern));
    }

    protected FsResult<Regex> CompileSearchRegex(string query, bool regex) =>
        SearchRegex.Compile(query, regex, SearchMatchTimeout);

    // The scan every backend used to reimplement: walk the scoped nodes, stop at maxResults, count
    // files searched and matched, and report a truncation. `render` returns the path to report and
    // the searchable text, or null content for a node that has nothing to search (a binary blob).
    protected async Task<FsResult<FsSearchResult>> SearchNodesAsync<TNode>(
        IEnumerable<TNode> nodes,
        Func<TNode, CancellationToken, ValueTask<(string Path, string? Content)>> render,
        FsSearchScan scan,
        CancellationToken ct)
    {
        if (!CompileSearchRegex(scan.Query, scan.Regex).TryGetValue(out var matcher, out var compileError))
        {
            return new FsResult<FsSearchResult>.Err(compileError);
        }

        var results = new List<FsSearchFileResult>();
        var totalMatches = 0;
        var filesWithMatches = 0;
        var filesSearched = 0;
        var truncated = false;

        try
        {
            foreach (var node in nodes)
            {
                var (path, content) = await render(node, ct);
                if (content is null)
                {
                    continue;
                }

                if (totalMatches >= scan.MaxResults)
                {
                    truncated = true;
                    break;
                }

                filesSearched++;
                var (matches, more) = VfsContentSearch.FindMatches(
                    content.Split('\n'), matcher, scan.ContextLines, scan.MaxResults - totalMatches);
                truncated |= more;
                if (matches.Count == 0)
                {
                    continue;
                }

                filesWithMatches++;
                totalMatches += matches.Count;
                results.Add(VfsContentSearch.BuildFileResult(path, matches, scan.OutputMode));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new FsResult<FsSearchResult>.Err(SearchRegex.TimedOut(scan.Query));
        }

        return new FsResult<FsSearchResult>.Ok(new FsSearchResult
        {
            Query = scan.Query,
            Regex = scan.Regex,
            Path = scan.Path,
            FilesSearched = filesSearched,
            FilesWithMatches = filesWithMatches,
            TotalMatches = totalMatches,
            Truncated = truncated,
            Results = results
        });
    }

    protected static FsResult<FsGlobResult> Glob(IReadOnlyList<string> entries) =>
        new FsResult<FsGlobResult>.Ok(new FsGlobResult
        {
            Entries = entries,
            Truncated = false,
            Total = entries.Count
        });

    private string UnsupportedMessage(string operation) =>
        $"The {FilesystemName} filesystem does not support '{operation}'.";
}