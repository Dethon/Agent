using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;

namespace Domain.Tools.FileSystem;

public class VfsCopyTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "copy";
    public const string Name = "copy";

    public const string ToolDescription = """
        Copies a file or directory between any two virtual paths, including across different filesystems.
        Same-filesystem copies use the backend's native primitive. Cross-filesystem copies stream content
        through the agent. Directory sources are recursed automatically. Best-effort: per-file failures
        do not abort the rest of the transfer.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to source file or directory")] string sourcePath,
        [Description("Virtual path to destination")] string destinationPath,
        [Description("Overwrite destination if it exists (default: false)")] bool overwrite = false,
        [Description("Create destination parent directories if missing (default: true)")] bool createDirectories = true,
        CancellationToken cancellationToken = default)
    {
        if (!registry.Resolve(sourcePath).TryGetValue(out var src, out var unresolvedSource))
        {
            return unresolvedSource.ToNode();
        }

        if (!registry.Resolve(destinationPath).TryGetValue(out var dst, out var unresolvedDestination))
        {
            return unresolvedDestination.ToNode();
        }

        var infoResult = await src.Backend.InfoAsync(src.RelativePath, cancellationToken);
        if (!infoResult.TryGetValue(out var info, out var infoError))
        {
            return infoError.ToNode();
        }
        var isDirectory = info.IsDirectory == true;

        if (isDirectory)
        {
            return await TransferDirectoryAsync(src, dst, sourcePath, destinationPath,
                overwrite, createDirectories, deleteSource: false, cancellationToken);
        }

        return await TransferFileAsync(src, dst, sourcePath, destinationPath,
            overwrite, createDirectories, deleteSource: false, cancellationToken);
    }

    internal static async Task<JsonNode> TransferFileAsync(
        FileSystemResolution src, FileSystemResolution dst,
        string srcVirtual, string dstVirtual,
        bool overwrite, bool createDirectories, bool deleteSource,
        CancellationToken ct)
    {
        if (ReferenceEquals(src.Backend, dst.Backend))
        {
            if (deleteSource)
            {
                var moveResult = await src.Backend.MoveAsync(src.RelativePath, dst.RelativePath, ct);
                if (!moveResult.TryGetValue(out _, out var moveError))
                {
                    return moveError.ToNode();
                }
                // No byte count: the native move measured nothing, and the field is omitted rather
                // than filled with a sentinel the model has to know to ignore.
                return Transferred(srcVirtual, dstVirtual, bytes: null);
            }

            var copyResult = await src.Backend.CopyAsync(src.RelativePath, dst.RelativePath,
                overwrite, createDirectories, ct);
            if (!copyResult.TryGetValue(out var copy, out var copyError))
            {
                return copyError.ToNode();
            }
            return Transferred(srcVirtual, dstVirtual, copy.Bytes);
        }

        if (deleteSource && await MoveOutRefusalAsync(src, ct) is { } refusal)
        {
            return refusal.ToNode();
        }

        long bytes;
        try
        {
            bytes = await dst.Backend.WriteChunksAsync(
                dst.RelativePath,
                src.Backend.ReadChunksAsync(src.RelativePath, ct),
                overwrite, createDirectories, ct);
        }
        catch (NotSupportedException ex)
        {
            // A non-disk backend (e.g. /ha, /schedules) can't take part in a streamed cross-mount
            // transfer. Surface that as the standard envelope instead of leaking the raw exception,
            // and leave the source untouched.
            return ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                $"Cannot transfer between '{srcVirtual}' and '{dstVirtual}': {ex.Message}",
                retryable: false,
                hint: "One of these filesystems does not support raw byte streaming, so it cannot be a " +
                      "source or destination for a cross-filesystem copy or move.");
        }
        catch (FileSystemOperationException ex)
        {
            // The backend knew exactly why it refused and had no envelope to say so in. Its code and
            // its retryability are the answer: a denied path or a disallowed file type is permanent,
            // and the catch-all below would invite the agent to retry it forever.
            return ToolError.Create(
                ex.Error.ErrorCode,
                $"Cannot transfer '{srcVirtual}' to '{dstVirtual}': {ex.Error.Message}",
                retryable: ex.Error.Retryable,
                hint: ex.Error.Hint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The stream fails for more than an unsupported backend: a missing source (fs_info
            // reports exists=false without an error, so the transfer gets this far), a full disk, a
            // refused destination. The directory path reports those per entry; here there is one
            // entry, and it becomes the same envelope. The source is left untouched either way.
            return ToolError.Create(
                ToolError.Codes.InternalError,
                $"Cannot transfer '{srcVirtual}' to '{dstVirtual}': {ex.Message}",
                retryable: true,
                hint: "Check that the source exists and that the destination is writable.");
        }

        if (deleteSource &&
            !(await src.Backend.DeleteAsync(src.RelativePath, ct)).TryGetValue(out _, out var deleteError))
        {
            return SourceNotRemoved(srcVirtual, dstVirtual, deleteError);
        }

        return Transferred(srcVirtual, dstVirtual, bytes);
    }

    private static JsonNode Transferred(string srcVirtual, string dstVirtual, long? bytes) =>
        FsResultContract.ToNode(new FsTransferResult
        {
            Status = FsTransferResult.Ok,
            Source = srcVirtual,
            Destination = dstVirtual,
            Bytes = bytes
        });

    internal static async Task<JsonNode> TransferDirectoryAsync(
        FileSystemResolution src, FileSystemResolution dst,
        string srcVirtual, string dstVirtual,
        bool overwrite, bool createDirectories, bool deleteSource,
        CancellationToken ct)
    {
        if (ReferenceEquals(src.Backend, dst.Backend))
        {
            ToolErrorResult? nativeError;
            if (deleteSource)
            {
                var moveResult = await src.Backend.MoveAsync(src.RelativePath, dst.RelativePath, ct);
                moveResult.TryGetValue(out _, out nativeError);
                if (nativeError is not null)
                {
                    return nativeError.ToNode();
                }
                return Transferred(srcVirtual, dstVirtual, bytes: null);
            }

            var copyResult = await src.Backend.CopyAsync(src.RelativePath, dst.RelativePath, overwrite, createDirectories, ct);
            if (!copyResult.TryGetValue(out var copy, out nativeError))
            {
                return nativeError.ToNode();
            }
            return Transferred(srcVirtual, dstVirtual, copy.Bytes);
        }

        if (deleteSource && await MoveOutRefusalAsync(src, ct) is { } refusal)
        {
            return refusal.ToNode();
        }

        var globResult = await src.Backend.GlobAsync(src.RelativePath, "**/*", ct);
        if (!globResult.TryGetValue(out var glob, out var globError))
        {
            return globError.ToNode();
        }
        // A capped backend (e.g. a file mount truncating at 200) can't enumerate the whole tree.
        // Transferring the partial listing would silently drop files while reporting success, so abort.
        if (glob.Truncated)
        {
            return ToolError.Create(
                ToolError.Codes.InvalidArgument,
                $"Source directory '{srcVirtual}' has {glob.Total} entries, more than a single listing can " +
                "enumerate; copying it would silently drop files.",
                retryable: false,
                hint: "Copy smaller subdirectories individually.");
        }
        var entries = glob.Entries;

        var perEntry = new List<FsTransferEntry>();
        var transferred = 0;
        var failed = 0;
        long totalBytes = 0;

        foreach (var srcRel in entries)
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
                perEntry.Add(new FsTransferEntry
                {
                    Status = FsTransferResult.Failed,
                    Error = $"Glob entry '{srcRel}' is not under source directory '{srcVirtual}'; refusing to flatten."
                });
                failed++;
                continue;
            }

            var dstRel = $"{dst.RelativePath.TrimEnd('/')}/{tail}";
            var dstVirtualEntry = $"{dstVirtual.TrimEnd('/')}/{tail}";
            var srcVirtualEntry = $"{srcVirtual.TrimEnd('/')}/{tail}";

            try
            {
                var bytes = await dst.Backend.WriteChunksAsync(
                    dstRel,
                    src.Backend.ReadChunksAsync(srcRel, ct),
                    overwrite, createDirectories, ct);

                perEntry.Add(new FsTransferEntry
                {
                    Status = FsTransferResult.Ok,
                    Source = srcVirtualEntry,
                    Destination = dstVirtualEntry,
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
                perEntry.Add(new FsTransferEntry
                {
                    Status = FsTransferResult.Failed,
                    Source = srcVirtualEntry,
                    Destination = dstVirtualEntry,
                    Error = ex.Message
                });
                failed++;
            }
        }

        // A move that streamed nothing is not a move: no destination was created, and the skipped
        // delete leaves the source in place — reporting "ok" would present that as done.
        if (deleteSource && transferred == 0 && failed == 0)
        {
            return ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                $"'{srcVirtual}' has no files to stream, and a cross-filesystem move cannot recreate " +
                "an empty directory on the destination; nothing was moved.",
                retryable: false,
                hint: "Create the directory on the destination filesystem instead, then remove the source.");
        }

        if (deleteSource && failed == 0 && transferred > 0 &&
            !(await src.Backend.DeleteAsync(src.RelativePath, ct)).TryGetValue(out _, out var deleteError))
        {
            return SourceNotRemoved(srcVirtual, dstVirtual, deleteError);
        }

        var status = (transferred, failed) switch
        {
            (_, 0) => FsTransferResult.Ok,
            (0, _) => FsTransferResult.Failed,
            _ => FsTransferResult.Partial
        };

        return FsResultContract.ToNode(new FsTransferResult
        {
            Status = status,
            Source = srcVirtual,
            Destination = dstVirtual,
            Summary = new FsTransferSummary
            {
                Transferred = transferred,
                Failed = failed,
                Skipped = 0,
                TotalBytes = totalBytes
            },
            Entries = perEntry
        });
    }

    // A cross-mount move is a streamed copy followed by a delete of the source, so the source
    // backend's own MoveAsync — where a refusal like "this path belongs to a live download" lives —
    // is never called. Both transfer paths ask here instead, under the delete-source condition that
    // is exactly what makes the question necessary, before the first byte and before the directory
    // listing. One question about the source root, not one per entry: a mount's rule covers a
    // subtree, and a round trip per file would buy only the download that goes live mid-transfer.
    private static async Task<ToolErrorResult?> MoveOutRefusalAsync(FileSystemResolution src, CancellationToken ct) =>
        (await src.Backend.MoveOutCheckAsync(src.RelativePath, ct)).TryGetValue(out _, out var refusal)
            ? null
            : refusal;

    // A streamed move is copy + delete; a refused delete must not present the duplicate-leaving
    // copy as a completed move. The envelope keeps the source's code so the caller can tell a
    // read-only refusal from a transient failure.
    private static JsonNode SourceNotRemoved(string srcVirtual, string dstVirtual, ToolErrorResult error) =>
        ToolError.Create(
            error.ErrorCode,
            $"Copied '{srcVirtual}' to '{dstVirtual}', but the source could not be removed: {error.Message}",
            retryable: error.Retryable,
            hint: $"The destination holds a complete copy. Remove '{srcVirtual}' yourself, or keep both copies.");

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