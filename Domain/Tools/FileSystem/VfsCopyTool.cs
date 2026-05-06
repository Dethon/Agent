using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

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

        var info = await src.Backend.InfoAsync(src.RelativePath, cancellationToken);
        var isDirectory = info["isDirectory"]?.GetValue<bool>() == true;

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
            JsonNode native;
            if (deleteSource)
            {
                native = await src.Backend.MoveAsync(src.RelativePath, dst.RelativePath, ct);
            }
            else
            {
                native = await src.Backend.CopyAsync(src.RelativePath, dst.RelativePath,
                    overwrite, createDirectories, ct);
            }
            return new JsonObject
            {
                ["status"] = "ok",
                ["source"] = srcVirtual,
                ["destination"] = dstVirtual,
                ["bytes"] = (native["bytes"] is JsonValue v && v.TryGetValue<long>(out var b) ? b : -1L)
            };
        }

        var bytes = await dst.Backend.WriteChunksAsync(
            dst.RelativePath,
            src.Backend.ReadChunksAsync(src.RelativePath, ct),
            overwrite, createDirectories, ct);

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
            var native = deleteSource
                ? await src.Backend.MoveAsync(src.RelativePath, dst.RelativePath, ct)
                : await src.Backend.CopyAsync(src.RelativePath, dst.RelativePath, overwrite, createDirectories, ct);

            return new JsonObject
            {
                ["status"] = "ok",
                ["source"] = srcVirtual,
                ["destination"] = dstVirtual,
                ["bytes"] = native["bytes"] is JsonValue v && v.TryGetValue<long>(out var b) ? b : -1L
            };
        }

        var glob = await src.Backend.GlobAsync(src.RelativePath, "**/*", VfsGlobMode.Files, ct);
        var entries = glob is JsonArray arr
            ? arr
            : (glob["entries"] as JsonArray ?? glob["files"] as JsonArray ?? new JsonArray());

        var perEntry = new JsonArray();
        var transferred = 0;
        var failed = 0;
        long totalBytes = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var srcRel = entry is JsonValue jv
                ? jv.GetValue<string>()
                : entry!["path"]!.GetValue<string>();
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

                if (deleteSource)
                {
                    await src.Backend.DeleteAsync(srcRel, ct);
                }

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
