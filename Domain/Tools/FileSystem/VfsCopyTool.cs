using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools;

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
        var src = registry.Resolve(sourcePath);
        var dst = registry.Resolve(destinationPath);

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
                return new JsonObject
                {
                    ["status"] = "ok",
                    ["source"] = srcVirtual,
                    ["destination"] = dstVirtual,
                    ["bytes"] = -1L // FsMoveResult carries no byte count
                };
            }

            var copyResult = await src.Backend.CopyAsync(src.RelativePath, dst.RelativePath,
                overwrite, createDirectories, ct);
            if (!copyResult.TryGetValue(out var copy, out var copyError))
            {
                return copyError.ToNode();
            }
            return new JsonObject
            {
                ["status"] = "ok",
                ["source"] = srcVirtual,
                ["destination"] = dstVirtual,
                ["bytes"] = copy.Bytes
            };
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

        if (deleteSource)
        {
            await src.Backend.DeleteAsync(src.RelativePath, ct);
        }

        return new JsonObject
        {
            ["status"] = "ok",
            ["source"] = srcVirtual,
            ["destination"] = dstVirtual,
            ["bytes"] = bytes
        };
    }

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
                return new JsonObject
                {
                    ["status"] = "ok",
                    ["source"] = srcVirtual,
                    ["destination"] = dstVirtual,
                    ["bytes"] = -1L // FsMoveResult carries no byte count
                };
            }

            var copyResult = await src.Backend.CopyAsync(src.RelativePath, dst.RelativePath, overwrite, createDirectories, ct);
            if (!copyResult.TryGetValue(out var copy, out nativeError))
            {
                return nativeError.ToNode();
            }
            return new JsonObject
            {
                ["status"] = "ok",
                ["source"] = srcVirtual,
                ["destination"] = dstVirtual,
                ["bytes"] = copy.Bytes
            };
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

        var perEntry = new JsonArray();
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
                perEntry.Add(new JsonObject
                {
                    ["source"] = srcRel,
                    ["status"] = "failed",
                    ["error"] = $"Glob entry '{srcRel}' is not under source directory '{src.RelativePath}'; refusing to flatten."
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

                perEntry.Add(new JsonObject
                {
                    ["source"] = srcVirtualEntry,
                    ["destination"] = dstVirtualEntry,
                    ["status"] = "ok",
                    ["bytes"] = bytes
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
                perEntry.Add(new JsonObject
                {
                    ["source"] = srcVirtualEntry,
                    ["destination"] = dstVirtualEntry,
                    ["status"] = "failed",
                    ["error"] = ex.Message
                });
                failed++;
            }
        }

        if (deleteSource && failed == 0 && transferred > 0)
        {
            await src.Backend.DeleteAsync(src.RelativePath, ct);
        }

        var status = (transferred, failed) switch
        {
            (_, 0) => "ok",
            (0, _) => "failed",
            _ => "partial"
        };

        return new JsonObject
        {
            ["status"] = status,
            ["summary"] = new JsonObject
            {
                ["transferred"] = transferred,
                ["failed"] = failed,
                ["skipped"] = 0,
                ["totalBytes"] = totalBytes
            },
            ["entries"] = perEntry
        };
    }

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