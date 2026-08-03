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
    IFileSystemClient client,
    LibraryPathConfig root,
    string[] allowedExtensions) : DiskFileSystem(filesystemName, client, root)
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

    protected override FsResult<FsReadResult> ReadFromDisk(string path, int? offset, int? limit) =>
        _read.Run(path, offset, limit);

    public override Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite,
        bool createDirectories, CancellationToken ct) =>
        Task.FromResult(_create.Run(path, content, overwrite, createDirectories));

    public override Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct) =>
        Task.FromResult(_edit.Run(path, edits));

    public override Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path,
        string? directoryPath, string? filePattern, int maxResults, int contextLines,
        VfsTextSearchOutputMode outputMode, CancellationToken ct) =>
        Task.FromResult(_search.Run(
            query, regex, path, filePattern, directoryPath ?? "/", maxResults, contextLines, outputMode));
}