using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Printing;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Printing.Vfs;

public sealed class PrinterQueueFileSystem(
    IPrintSpool spool,
    IPrinterClient printer,
    PrintQueueCoordinator coordinator,
    string supportedFormats) : IFileSystemBackend
{
    public string FilesystemName => "print-queue";

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        await coordinator.ReconcileAsync(ct);
        var node = PrinterQueuePath.Parse(path);

        if (node.Kind == PrinterNodeKind.StatusFile)
        {
            var status = await RenderStatusAsync(ct);
            return Ok(path, status);
        }

        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            return NotFound<FsReadResult>(path);
        }

        var bytes = await spool.ReadAllBytesAsync(node.FileName!, ct);
        if (bytes is null)
        {
            return NotFound<FsReadResult>(path);
        }

        if (!IsText(bytes))
        {
            return new FsResult<FsReadResult>.Err(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.UnsupportedOperation,
                Message = $"'{node.FileName}' is a binary document and cannot be read as text.",
                Retryable = false,
                Hint = "Read /print-queue/status.json for its print state, or use fs_blob_read for raw bytes."
            });
        }

        return Ok(path, Encoding.UTF8.GetString(bytes));
    }

    public async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        await coordinator.ReconcileAsync(ct);
        var node = PrinterQueuePath.Parse(path);

        if (node.Kind == PrinterNodeKind.Root)
        {
            return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = path, IsDirectory = true });
        }

        if (node.Kind == PrinterNodeKind.StatusFile)
        {
            return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = path, IsDirectory = false });
        }

        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = false, Path = path });
        }

        var entry = await spool.GetAsync(node.FileName!, ct);
        if (entry is null)
        {
            return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = false, Path = path });
        }

        return new FsResult<FsInfoResult>.Ok(new FsInfoResult
        {
            Exists = true,
            Path = path,
            IsDirectory = false,
            Size = entry.SizeBytes,
            LastModified = entry.LastWriteAt.UtcDateTime.ToString("O")
        });
    }

    public async Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        var node = PrinterQueuePath.Parse(path);
        if (node.Kind == PrinterNodeKind.StatusFile)
        {
            return ReadOnly<FsCreateResult>(path);
        }

        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            return Invalid<FsCreateResult>($"Create a document at /print-queue/<filename> (got '{path}')");
        }

        if (!overwrite && await spool.GetAsync(node.FileName!, ct) is not null)
        {
            return new FsResult<FsCreateResult>.Err(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.AlreadyExists,
                Message = $"A job named '{node.FileName}' is already queued. Pass overwrite=true to replace it.",
                Retryable = false
            });
        }

        await CancelIfSubmittedAsync(node.FileName!, ct);
        var bytes = Encoding.UTF8.GetBytes(content);
        await spool.WriteBytesAsync(node.FileName!, "text/plain", bytes, 0, true, ct);

        return new FsResult<FsCreateResult>.Ok(new FsCreateResult
        {
            Status = "queued",
            FilePath = path,
            Size = bytes.Length.ToString(),
            Lines = content.Split('\n').Length
        });
    }

    public async Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct)
    {
        var node = PrinterQueuePath.Parse(path);
        if (node.Kind == PrinterNodeKind.StatusFile)
        {
            return ReadOnly<FsEditResult>(path);
        }

        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            return NotFound<FsEditResult>(path);
        }

        var bytes = await spool.ReadAllBytesAsync(node.FileName!, ct);
        if (bytes is null)
        {
            return NotFound<FsEditResult>(path);
        }

        if (!IsText(bytes))
        {
            return Unsupported<FsEditResult>($"'{node.FileName}' is a binary document and cannot be edited as text.");
        }

        var text = Encoding.UTF8.GetString(bytes);
        var total = 0;
        var details = new List<FsEditDetail>();
        foreach (var edit in edits)
        {
            var count = CountOccurrences(text, edit.OldString);
            if (count == 0)
            {
                return Invalid<FsEditResult>($"Text not found: '{edit.OldString}'");
            }

            var replaced = edit.ReplaceAll ? count : 1;
            text = ReplaceFirstOrAll(text, edit.OldString, edit.NewString, edit.ReplaceAll);
            total += replaced;
            details.Add(new FsEditDetail { OccurrencesReplaced = replaced, AffectedLines = new FsLineRange { Start = 0, End = 0 } });
        }

        // Replacing the spooled bytes (offset 0) resets the lifecycle to unsubmitted; cancel any in-flight job first.
        await CancelIfSubmittedAsync(node.FileName!, ct);
        await spool.WriteBytesAsync(node.FileName!, "text/plain", Encoding.UTF8.GetBytes(text), 0, true, ct);

        return new FsResult<FsEditResult>.Ok(new FsEditResult
        {
            Status = "queued",
            FilePath = path,
            TotalOccurrencesReplaced = total,
            Edits = details
        });
    }

    public async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        await coordinator.ReconcileAsync(ct);
        var entries = await spool.ListAsync(ct);
        var names = entries.Select(e => e.FileName).Append(PrinterQueuePath.StatusFileName);

        var matches = GlobRegex.CompileMatcher(pattern);
        var matched = names.Where(matches).OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => "/" + n).ToList();

        return new FsResult<FsGlobResult>.Ok(new FsGlobResult
        {
            Entries = matched,
            Truncated = false,
            Total = matched.Count
        });
    }

    public async Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path, string? directoryPath,
        string? filePattern, int maxResults, int contextLines, VfsTextSearchOutputMode outputMode, CancellationToken ct)
    {
        await coordinator.ReconcileAsync(ct);

        Regex matcher;
        try
        {
            matcher = new Regex(regex ? query : Regex.Escape(query), RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return Invalid<FsSearchResult>($"Invalid regex: {ex.Message}");
        }

        var entries = await spool.ListAsync(ct);
        var results = new List<FsSearchFileResult>();
        var totalMatches = 0;
        var filesWithMatches = 0;
        var filesSearched = 0;
        var truncated = false;

        foreach (var entry in entries)
        {
            if (!VfsContentSearch.MatchesFilePattern(filePattern, entry.FileName))
            {
                continue;
            }

            var bytes = await spool.ReadAllBytesAsync(entry.FileName, ct);
            if (bytes is null || !IsText(bytes))
            {
                continue;
            }

            if (totalMatches >= maxResults)
            {
                truncated = true;
                break;
            }

            filesSearched++;
            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            var (matches, more) = VfsContentSearch.FindMatches(lines, matcher, contextLines, maxResults - totalMatches);
            truncated |= more;
            if (matches.Count == 0)
            {
                continue;
            }

            filesWithMatches++;
            totalMatches += matches.Count;
            results.Add(VfsContentSearch.BuildFileResult("/" + entry.FileName, matches, outputMode));
        }

        return new FsResult<FsSearchResult>.Ok(new FsSearchResult
        {
            Query = query,
            Regex = regex,
            Path = path ?? directoryPath ?? "/",
            FilesSearched = filesSearched,
            FilesWithMatches = filesWithMatches,
            TotalMatches = totalMatches,
            Truncated = truncated,
            Results = results
        });
    }

    public Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsMoveResult>(
            "The print queue does not support move. Copy a document into /print-queue to print it."));

    public async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        var node = PrinterQueuePath.Parse(path);
        if (node.Kind == PrinterNodeKind.StatusFile)
        {
            return ReadOnly<FsRemoveResult>(path);
        }

        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            return NotFound<FsRemoveResult>(path);
        }

        var entry = await spool.GetAsync(node.FileName!, ct);
        if (entry is null)
        {
            return NotFound<FsRemoveResult>(path);
        }

        // The crux: cancelling before the printer finishes means it will not print.
        await CancelIfSubmittedAsync(node.FileName!, ct);
        await spool.RemoveAsync(node.FileName!, ct);

        return new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
        {
            Status = "removed",
            Message = entry.IsSubmitted ? "Print job cancelled and removed from the queue." : "Removed from the queue before printing.",
            OriginalPath = path,
            TrashPath = ""
        });
    }

    public Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsExecResult>("The print queue does not support exec."));

    public async Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        var src = PrinterQueuePath.Parse(sourcePath);
        var dst = PrinterQueuePath.Parse(destinationPath);
        if (src.Kind != PrinterNodeKind.DocumentFile || dst.Kind != PrinterNodeKind.DocumentFile)
        {
            return Invalid<FsCopyResult>("Copy within the print queue requires document file paths.");
        }

        var bytes = await spool.ReadAllBytesAsync(src.FileName!, ct);
        if (bytes is null)
        {
            return NotFound<FsCopyResult>(sourcePath);
        }

        if (!overwrite && await spool.GetAsync(dst.FileName!, ct) is not null)
        {
            return new FsResult<FsCopyResult>.Err(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.AlreadyExists,
                Message = $"A job named '{dst.FileName}' is already queued.",
                Retryable = false
            });
        }

        // No format check needed: the source is already a spooled document, so it passed the
        // format gate on the way in and cannot introduce an unsupported payload.
        var srcEntry = await spool.GetAsync(src.FileName!, ct);
        await CancelIfSubmittedAsync(dst.FileName!, ct);
        await spool.WriteBytesAsync(dst.FileName!, srcEntry!.ContentType, bytes, 0, true, ct);

        return new FsResult<FsCopyResult>.Ok(new FsCopyResult
        {
            Status = "queued",
            Source = sourcePath,
            Destination = destinationPath,
            Bytes = bytes.Length
        });
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var node = PrinterQueuePath.Parse(path);
        var bytes = node.Kind == PrinterNodeKind.DocumentFile
            ? await spool.ReadAllBytesAsync(node.FileName!, ct)
            : null;

        if (bytes is null)
        {
            yield break;
        }

        const int chunkSize = 256 * 1024;
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            yield return bytes.AsMemory(offset, Math.Min(chunkSize, bytes.Length - offset));
        }
    }

    public async Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        var node = PrinterQueuePath.Parse(path);
        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            throw new InvalidOperationException($"Cannot write to '{path}' in the print queue.");
        }

        long offset = 0;
        await CancelIfSubmittedAsync(node.FileName!, ct);
        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            // The first chunk carries the file header; reject formats the printer cannot render before
            // anything is spooled. The MCP fs_blob_write tool guards too, but enforcing it here keeps
            // the backend self-consistent for any in-process caller.
            if (offset == 0)
            {
                var format = PrintableContent.DetectFormat(chunk.Span);
                if (!PrintableContent.IsSupported(format, supportedFormats))
                {
                    throw new InvalidOperationException(
                        $"'{node.FileName}' looks like '{format}', which this printer cannot render. Supported formats: {supportedFormats}.");
                }
            }

            await spool.WriteBytesAsync(node.FileName!, "application/octet-stream", chunk, offset, overwrite && offset == 0, ct);
            offset += chunk.Length;
        }

        if (offset == 0)
        {
            await spool.WriteBytesAsync(node.FileName!, "application/octet-stream", ReadOnlyMemory<byte>.Empty, 0, true, ct);
        }

        return offset;
    }

    private async Task CancelIfSubmittedAsync(string fileName, CancellationToken ct)
    {
        var entry = await spool.GetAsync(fileName, ct);
        if (entry is { IsSubmitted: true })
        {
            await printer.CancelAsync(entry.JobId!.Value, ct);
        }
    }

    private async Task<string> RenderStatusAsync(CancellationToken ct)
    {
        var entries = await spool.ListAsync(ct);
        var active = (await printer.GetActiveJobsAsync(ct)).ToDictionary(j => j.JobId);

        var rows = entries.Select(e => new
        {
            filename = e.FileName,
            jobId = e.JobId,
            state = !e.IsSubmitted
                ? PrintJobState.Queued.ToString()
                : active.TryGetValue(e.JobId!.Value, out var job) ? job.State.ToString() : PrintJobState.Processing.ToString(),
            submittedAt = e.SubmittedAt?.UtcDateTime.ToString("O"),
            sizeBytes = e.SizeBytes
        }).OrderBy(r => r.filename);

        return JsonSerializer.Serialize(rows, _json);
    }

    private static bool SafeMatch(Regex matcher, string text)
    {
        try
        {
            return matcher.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool IsText(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return false;
        }

        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirstOrAll(string text, string oldValue, string newValue, bool all)
    {
        if (all)
        {
            return text.Replace(oldValue, newValue, StringComparison.Ordinal);
        }

        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? text : text[..index] + newValue + text[(index + oldValue.Length)..];
    }

    private static FsResult<FsReadResult> Ok(string path, string content) =>
        new FsResult<FsReadResult>.Ok(new FsReadResult
        {
            FilePath = path,
            Content = content,
            TotalLines = content.Split('\n').Length,
            Truncated = false
        });

    private static FsResult<T> ReadOnly<T>(string path) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.UnsupportedOperation, $"{path} is read-only"));

    private static FsResult<T> Invalid<T>(string message) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.InvalidArgument, message));

    private static FsResult<T> NotFound<T>(string path) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.NotFound, $"Path not found: {path}"));

    private static FsResult<T> Unsupported<T>(string message) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.UnsupportedOperation, message));

    private static ToolErrorResult Error(string code, string message) =>
        new() { ErrorCode = code, Message = message, Retryable = false };
}