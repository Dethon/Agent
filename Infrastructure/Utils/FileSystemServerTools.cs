using System.ComponentModel;
using System.Reflection;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Infrastructure.Utils;

// Registers a server's fs_* tools from its backend. A tool exists for exactly the operations the
// backend overrides, and its description comes from the hook next to that implementation — so a
// server cannot advertise an operation its backend does not implement, and there is no second
// place for the capability list to drift from.
//
// Capability is per operation, not per path: a backend may override an operation and still refuse
// particular paths. That coarseness is intended — the list tells the model which operations exist
// on a mount, not which will succeed on a given file.
public static class FileSystemServerTools
{
    private sealed record Operation(
        string ToolName,
        string MethodName,
        Func<FileSystemBackendBase, string> Describe,
        Func<FileSystemBackendBase, Delegate> Handler);

    private static readonly IReadOnlyList<Operation> _operations =
    [
        new("fs_read", nameof(IFileSystemBackend.ReadAsync), b => b.DescribeRead, b =>
            async (string path, int? offset, int? limit, CancellationToken ct) =>
                ToolResponse.Create(await b.ReadAsync(path, offset, limit, ct))),

        new("fs_info", nameof(IFileSystemBackend.InfoAsync), b => b.DescribeInfo, b =>
            async (string path, CancellationToken ct) =>
                ToolResponse.Create(await b.InfoAsync(path, ct))),

        new("fs_glob", nameof(IFileSystemBackend.GlobAsync), b => b.DescribeGlob, b =>
            async (string pattern, string basePath, CancellationToken ct) =>
                ToolResponse.Create(await b.GlobAsync(basePath, pattern, ct))),

        new("fs_search", nameof(IFileSystemBackend.SearchAsync), b => b.DescribeSearch, b =>
            async (string query, bool regex, string? path, string? directoryPath, string? filePattern,
                    int maxResults, int contextLines, string outputMode, CancellationToken ct) =>
                ToolResponse.Create(await b.SearchAsync(
                    query, regex, path, directoryPath, filePattern, maxResults, contextLines,
                    ParseOutputMode(outputMode), ct))),

        new("fs_create", nameof(IFileSystemBackend.CreateAsync), b => b.DescribeCreate, b =>
            async (string path, string content, bool overwrite, bool createDirectories, CancellationToken ct) =>
                ToolResponse.Create(await b.CreateAsync(path, content, overwrite, createDirectories, ct))),

        new("fs_edit", nameof(IFileSystemBackend.EditAsync), b => b.DescribeEdit, b =>
            async (string path, IReadOnlyList<TextEdit> edits, CancellationToken ct) =>
                ToolResponse.Create(await b.EditAsync(path, edits, ct))),

        new("fs_move", nameof(IFileSystemBackend.MoveAsync), b => b.DescribeMove, b =>
            async (string sourcePath, string destinationPath, CancellationToken ct) =>
                ToolResponse.Create(await b.MoveAsync(sourcePath, destinationPath, ct))),

        new("fs_delete", nameof(IFileSystemBackend.DeleteAsync), b => b.DescribeDelete, b =>
            async (string path, CancellationToken ct) =>
                ToolResponse.Create(await b.DeleteAsync(path, ct))),

        new("fs_exec", nameof(IFileSystemBackend.ExecAsync), b => b.DescribeExec, b =>
            async (string path, string command, int? timeoutSeconds, CancellationToken ct) =>
                ToolResponse.Create(await b.ExecAsync(path, command, timeoutSeconds, ct))),

        new("fs_copy", nameof(IFileSystemBackend.CopyAsync), b => b.DescribeCopy, b =>
            async (string sourcePath, string destinationPath, bool overwrite, bool createDirectories, CancellationToken ct) =>
                ToolResponse.Create(await b.CopyAsync(sourcePath, destinationPath, overwrite, createDirectories, ct))),

        new("fs_blob_read", nameof(IFileSystemBackend.ReadChunksAsync), b => b.DescribeBlobRead, b =>
            async (string path, long offset, int length, CancellationToken ct) =>
                ToolResponse.Create(await b.ReadBlobAsync(path, offset, length, ct))),

        new("fs_blob_write", nameof(IFileSystemBackend.WriteChunksAsync), b => b.DescribeBlobWrite, b =>
            async (string path, string contentBase64, long offset, bool overwrite, bool createDirectories, CancellationToken ct) =>
                ToolResponse.Create(await b.WriteBlobAsync(path, contentBase64, offset, overwrite, createDirectories, ct)))
    ];

    public static IMcpServerBuilder AddFileSystemTools<TBackend>(this IMcpServerBuilder builder)
        where TBackend : FileSystemBackendBase
    {
        foreach (var operation in _operations.Where(o => Overrides(typeof(TBackend), o.MethodName)))
        {
            builder.Services.AddSingleton<McpServerTool>(sp =>
            {
                var backend = sp.GetRequiredService<TBackend>();
                return McpServerTool.Create(operation.Handler(backend), new McpServerToolCreateOptions
                {
                    Name = operation.ToolName,
                    Description = operation.Describe(backend)
                });
            });
        }

        return builder;
    }

    // The operations a backend really implements, in the order the fs_* tools are declared.
    public static IReadOnlyList<string> SupportedToolNames(Type backendType) =>
        _operations.Where(o => Overrides(backendType, o.MethodName)).Select(o => o.ToolName).ToList();

    // The backend's own declaration of what it can do. An operation it never overrode is still the
    // base's unsupported default, so there is nothing to register.
    private static bool Overrides(Type backendType, string methodName) =>
        backendType
            .GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?.DeclaringType != typeof(FileSystemBackendBase);

    private static VfsTextSearchOutputMode ParseOutputMode(string outputMode) =>
        outputMode.Equals("filesOnly", StringComparison.OrdinalIgnoreCase)
            ? VfsTextSearchOutputMode.FilesOnly
            : VfsTextSearchOutputMode.Content;
}