using Domain.Contracts;
using Domain.DTOs.FileSystem;

namespace Domain.Tools.FileSystem;

// Which of the two a transfer is. It used to be a boolean called deleteSource, which gated three
// separate rules that have nothing to do with deleting: whether to ask the source's move-out
// question, whether to remove the source afterwards, and whether a directory that streamed nothing
// counts as done. Named, each of those reads as a rule about a move.
public enum TransferIntent
{
    Copy,
    Move
}

public sealed record TransferRequest
{
    public required FileSystemResolution Source { get; init; }
    public required FileSystemResolution Destination { get; init; }

    // The caller's own strings, echoed into every path the answer reports and into every message
    // this module builds. See docs/adr/0016-a-tool-answers-in-the-coordinates-it-was-asked-in.md.
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }

    public required TransferIntent Intent { get; init; }
    public bool Overwrite { get; init; }
    public bool CreateDirectories { get; init; } = true;

    public bool IsMove => Intent is TransferIntent.Move;
}

// A copy or a move across any two virtual paths — same mount or not, file or directory. One entry
// point, and it owns the file-versus-directory decision, so the two tools that call it shrink to
// resolving both ends, delegating, and serializing. Everything comes back inside the standard
// result union, so an error stays typed until the tool boundary.
internal static class Transfer
{
    public static async Task<FsResult<FsTransferResult>> RunAsync(TransferRequest request, CancellationToken ct)
    {
        var infoResult = await request.Source.Backend.InfoAsync(request.Source.RelativePath, ct);
        if (!infoResult.TryGetValue(out var info, out var infoError))
        {
            return new FsResult<FsTransferResult>.Err(infoError);
        }

        return info.IsDirectory == true
            ? await DirectoryAsync(request, ct)
            : await FileAsync(request, ct);
    }

    private static async Task<FsResult<FsTransferResult>> FileAsync(TransferRequest request, CancellationToken ct)
    {
        var (src, dst) = (request.Source, request.Destination);

        if (ReferenceEquals(src.Backend, dst.Backend))
        {
            return await NativeAsync(request, ct);
        }

        if (request.IsMove && await MoveOutRefusalAsync(src, ct) is { } refusal)
        {
            return new FsResult<FsTransferResult>.Err(refusal);
        }

        long bytes;
        try
        {
            bytes = await dst.Backend.WriteChunksAsync(
                dst.RelativePath,
                src.Backend.ReadChunksAsync(src.RelativePath, ct),
                request.Overwrite, request.CreateDirectories, ct);
        }
        catch (NotSupportedException ex)
        {
            // A non-disk backend (e.g. /ha, /schedules) can't take part in a streamed cross-mount
            // transfer. Surface that as the standard envelope instead of leaking the raw exception,
            // and leave the source untouched.
            return Fail(
                ToolError.Codes.UnsupportedOperation,
                $"Cannot transfer between '{request.SourcePath}' and '{request.DestinationPath}': {ex.Message}",
                retryable: false,
                hint: "One of these filesystems does not support raw byte streaming, so it cannot be a " +
                      "source or destination for a cross-filesystem copy or move.");
        }
        catch (FileSystemOperationException ex)
        {
            // The backend knew exactly why it refused and had no envelope to say so in. Its code and
            // its retryability are the answer: a denied path or a disallowed file type is permanent,
            // and the catch-all below would invite the agent to retry it forever.
            return Fail(
                ex.Error.ErrorCode,
                $"Cannot transfer '{request.SourcePath}' to '{request.DestinationPath}': {ex.Error.Message}",
                retryable: ex.Error.Retryable,
                hint: ex.Error.Hint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The stream fails for more than an unsupported backend: a missing source (fs_info
            // reports exists=false without an error, so the transfer gets this far), a full disk, a
            // refused destination. The directory path reports those per entry; here there is one
            // entry, and it becomes the same envelope. The source is left untouched either way.
            return Fail(
                ToolError.Codes.InternalError,
                $"Cannot transfer '{request.SourcePath}' to '{request.DestinationPath}': {ex.Message}",
                retryable: true,
                hint: "Check that the source exists and that the destination is writable.");
        }

        if (request.IsMove &&
            !(await src.Backend.DeleteAsync(src.RelativePath, ct)).TryGetValue(out _, out var deleteError))
        {
            return SourceNotRemoved(request, deleteError);
        }

        return Transferred(request, bytes);
    }

    private static async Task<FsResult<FsTransferResult>> DirectoryAsync(TransferRequest request, CancellationToken ct)
    {
        var (src, dst) = (request.Source, request.Destination);

        if (ReferenceEquals(src.Backend, dst.Backend))
        {
            return await NativeAsync(request, ct);
        }

        if (request.IsMove && await MoveOutRefusalAsync(src, ct) is { } refusal)
        {
            return new FsResult<FsTransferResult>.Err(refusal);
        }

        var globResult = await src.Backend.GlobAsync(src.RelativePath, "**/*", ct);
        if (!globResult.TryGetValue(out var glob, out var globError))
        {
            return new FsResult<FsTransferResult>.Err(globError);
        }

        // A capped backend (e.g. a file mount truncating at 200) can't enumerate the whole tree.
        // Transferring the partial listing would silently drop files while reporting success, so abort.
        if (glob.Truncated)
        {
            return Fail(
                ToolError.Codes.InvalidArgument,
                $"Source directory '{request.SourcePath}' has {glob.Total} entries, more than a single listing can " +
                "enumerate; copying it would silently drop files.",
                retryable: false,
                hint: "Copy smaller subdirectories individually.");
        }

        var entries = new List<FsTransferEntry>();
        var transferred = 0;
        var failed = 0;
        long totalBytes = 0;

        foreach (var srcRel in glob.Entries)
        {
            ct.ThrowIfCancellationRequested();
            // Pure glob returns directory marker entries (trailing '/') alongside files.
            // Directories carry no content and are recreated implicitly when their files are
            // written (createDirectories), so they are not transfer candidates.
            if (srcRel.EndsWith('/'))
            {
                continue;
            }

            var tail = ExtractTail(srcRel, src.RelativePath);
            if (tail is null)
            {
                // The one entry with no virtual path: something outside the requested source
                // directory is outside the coordinate frame, so there is nothing to translate. It
                // reports no source at all, and the backend's raw string goes in the message, where
                // it reads as diagnostics rather than as a path to retry.
                entries.Add(new FsTransferEntry
                {
                    Status = FsTransferResult.Failed,
                    Error = $"Glob entry '{srcRel}' is not under source directory '{request.SourcePath}'; refusing to flatten."
                });
                failed++;
                continue;
            }

            var entrySource = $"{request.SourcePath.TrimEnd('/')}/{tail}";
            var entryDestination = $"{request.DestinationPath.TrimEnd('/')}/{tail}";

            try
            {
                var bytes = await dst.Backend.WriteChunksAsync(
                    $"{dst.RelativePath.TrimEnd('/')}/{tail}",
                    src.Backend.ReadChunksAsync(srcRel, ct),
                    request.Overwrite, request.CreateDirectories, ct);

                entries.Add(new FsTransferEntry
                {
                    Status = FsTransferResult.Ok,
                    Source = entrySource,
                    Destination = entryDestination,
                    Bytes = bytes
                });
                transferred++;
                totalBytes += bytes;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                entries.Add(new FsTransferEntry
                {
                    Status = FsTransferResult.Failed,
                    Source = entrySource,
                    Destination = entryDestination,
                    Error = ex.Message
                });
                failed++;
            }
        }

        // A move that streamed nothing is not a move: no destination was created, and the skipped
        // delete leaves the source in place — reporting "ok" would present that as done.
        if (request.IsMove && transferred == 0 && failed == 0)
        {
            return Fail(
                ToolError.Codes.UnsupportedOperation,
                $"'{request.SourcePath}' has no files to stream, and a cross-filesystem move cannot recreate " +
                "an empty directory on the destination; nothing was moved.",
                retryable: false,
                hint: "Create the directory on the destination filesystem instead, then remove the source.");
        }

        if (request.IsMove && failed == 0 && transferred > 0 &&
            !(await src.Backend.DeleteAsync(src.RelativePath, ct)).TryGetValue(out _, out var deleteError))
        {
            return SourceNotRemoved(request, deleteError);
        }

        return new FsResult<FsTransferResult>.Ok(new FsTransferResult
        {
            Status = (transferred, failed) switch
            {
                (_, 0) => FsTransferResult.Ok,
                (0, _) => FsTransferResult.Failed,
                _ => FsTransferResult.Partial
            },
            Source = request.SourcePath,
            Destination = request.DestinationPath,
            Summary = new FsTransferSummary
            {
                Transferred = transferred,
                Failed = failed,
                Skipped = 0,
                TotalBytes = totalBytes
            },
            Entries = entries
        });
    }

    // Both ends on one mount, so the backend's own primitive does the work — and for a move that is
    // where its refusals already live, which is why the move-out question is not asked here.
    private static async Task<FsResult<FsTransferResult>> NativeAsync(TransferRequest request, CancellationToken ct)
    {
        var (src, dst) = (request.Source, request.Destination);

        if (request.IsMove)
        {
            var moveResult = await src.Backend.MoveAsync(src.RelativePath, dst.RelativePath, ct);
            // No byte count: the native move measured nothing, and the field is omitted rather than
            // filled with a sentinel the model has to know to ignore.
            return moveResult.TryGetValue(out _, out var moveError)
                ? Transferred(request, bytes: null)
                : new FsResult<FsTransferResult>.Err(moveError);
        }

        var copyResult = await src.Backend.CopyAsync(
            src.RelativePath, dst.RelativePath, request.Overwrite, request.CreateDirectories, ct);
        return copyResult.TryGetValue(out var copy, out var copyError)
            ? Transferred(request, copy.Bytes)
            : new FsResult<FsTransferResult>.Err(copyError);
    }

    // A cross-mount move is a streamed copy followed by a delete of the source, so the source
    // backend's own MoveAsync — where a refusal like "this path belongs to a live download" lives —
    // is never called. Both branches ask here instead, under the intent that is exactly what makes
    // the question necessary, before the first byte and before the directory listing. One question
    // about the source root, not one per entry: a mount's rule covers a subtree, and a round trip
    // per file would buy only the download that goes live mid-transfer.
    private static async Task<ToolErrorResult?> MoveOutRefusalAsync(FileSystemResolution src, CancellationToken ct) =>
        (await src.Backend.MoveOutCheckAsync(src.RelativePath, ct)).TryGetValue(out _, out var refusal)
            ? null
            : refusal;

    private static FsResult<FsTransferResult> Transferred(TransferRequest request, long? bytes) =>
        new FsResult<FsTransferResult>.Ok(new FsTransferResult
        {
            Status = FsTransferResult.Ok,
            Source = request.SourcePath,
            Destination = request.DestinationPath,
            Bytes = bytes
        });

    // A streamed move is copy + delete; a refused delete must not present the duplicate-leaving
    // copy as a completed move. The envelope keeps the source's code so the caller can tell a
    // read-only refusal from a transient failure.
    private static FsResult<FsTransferResult> SourceNotRemoved(TransferRequest request, ToolErrorResult error) =>
        Fail(
            error.ErrorCode,
            $"Copied '{request.SourcePath}' to '{request.DestinationPath}', but the source could not be removed: {error.Message}",
            retryable: error.Retryable,
            hint: $"The destination holds a complete copy. Remove '{request.SourcePath}' yourself, or keep both copies.");

    private static FsResult<FsTransferResult> Fail(string code, string message, bool retryable, string? hint) =>
        FsError.Fail<FsTransferResult>(code, message, retryable, hint);

    private static string? ExtractTail(string srcRel, string sourceDir)
    {
        var normalized = srcRel.Replace('\\', '/');
        var dir = sourceDir.Trim('/');
        if (string.IsNullOrEmpty(dir))
        {
            var rooted = normalized.TrimStart('/');
            return string.IsNullOrEmpty(rooted) ? null : rooted;
        }

        var prefix = dir + "/";
        if (normalized.StartsWith(prefix, StringComparison.Ordinal))
        {
            var tail = normalized[prefix.Length..];
            return string.IsNullOrEmpty(tail) ? null : tail;
        }

        var marker = "/" + dir + "/";
        var idx = normalized.IndexOf(marker, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var tail = normalized[(idx + marker.Length)..];
            return string.IsNullOrEmpty(tail) ? null : tail;
        }

        return null;
    }
}