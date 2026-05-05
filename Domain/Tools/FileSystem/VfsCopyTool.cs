using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

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
        var isDirectory = info["type"]?.GetValue<string>() == "directory";

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
                ["bytes"] = native["bytes"]?.DeepClone()
            };
        }

        long bytes;
        await using (var stream = await src.Backend.OpenReadStreamAsync(src.RelativePath, ct))
        {
            await dst.Backend.WriteFromStreamAsync(dst.RelativePath, stream, overwrite, createDirectories, ct);
            bytes = stream.CanSeek ? stream.Length : -1;
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

    internal static Task<JsonNode> TransferDirectoryAsync(
        FileSystemResolution src, FileSystemResolution dst,
        string srcVirtual, string dstVirtual,
        bool overwrite, bool createDirectories, bool deleteSource,
        CancellationToken ct)
    {
        // Implemented in Task 13.
        throw new NotImplementedException();
    }
}
