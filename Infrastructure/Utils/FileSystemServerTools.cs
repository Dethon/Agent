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
    private sealed record Wiring(
        Func<FileSystemBackendBase, string> Describe,
        Func<FileSystemBackendBase, Delegate> Handler);

    // How each operation reaches the wire. The operations themselves — their names, their backend
    // methods — come from the one list; this only adds the tool signature and the description hook.
    private static readonly IReadOnlyDictionary<string, Wiring> _wiring = new Dictionary<string, Wiring>(StringComparer.Ordinal)
    {
        ["fs_read"] = new(b => b.DescribeRead, b =>
            async (string path, int? offset = null, int? limit = null, CancellationToken ct = default) =>
                ToolResponse.Create(await b.ReadAsync(path, offset, limit, ct))),

        ["fs_info"] = new(b => b.DescribeInfo, b =>
            async (string path, CancellationToken ct = default) =>
                ToolResponse.Create(await b.InfoAsync(path, ct))),

        ["fs_glob"] = new(b => b.DescribeGlob, b =>
            async (string pattern, string basePath = "", CancellationToken ct = default) =>
                ToolResponse.Create(await b.GlobAsync(basePath, pattern, ct))),

        ["fs_search"] = new(b => b.DescribeSearch, b =>
            async (string query, bool regex = false, string? path = null, string? directoryPath = null,
                    string? filePattern = null, int maxResults = 50, int contextLines = 1,
                    string outputMode = "content", CancellationToken ct = default) =>
                ToolResponse.Create(await b.SearchAsync(
                    query, regex, path, directoryPath, filePattern, maxResults, contextLines,
                    ParseOutputMode(outputMode), ct))),

        ["fs_create"] = new(b => b.DescribeCreate, b =>
            async (string path, string content, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default) =>
                ToolResponse.Create(await b.CreateAsync(path, content, overwrite, createDirectories, ct))),

        ["fs_edit"] = new(b => b.DescribeEdit, b =>
            async (string path, IReadOnlyList<TextEdit> edits, CancellationToken ct = default) =>
                ToolResponse.Create(await b.EditAsync(path, edits, ct))),

        ["fs_move"] = new(b => b.DescribeMove, b =>
            async (string sourcePath, string destinationPath, CancellationToken ct = default) =>
                ToolResponse.Create(await b.MoveAsync(sourcePath, destinationPath, ct))),

        ["fs_delete"] = new(b => b.DescribeDelete, b =>
            async (string path, CancellationToken ct = default) =>
                ToolResponse.Create(await b.DeleteAsync(path, ct))),

        ["fs_exec"] = new(b => b.DescribeExec, b =>
            async (string path, string command, int? timeoutSeconds = null, CancellationToken ct = default) =>
                ToolResponse.Create(await b.ExecAsync(path, command, timeoutSeconds, ct))),

        ["fs_copy"] = new(b => b.DescribeCopy, b =>
            async (string sourcePath, string destinationPath, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default) =>
                ToolResponse.Create(await b.CopyAsync(sourcePath, destinationPath, overwrite, createDirectories, ct))),

        ["fs_blob_read"] = new(b => b.DescribeBlobRead, b =>
            async (string path, long offset = 0, int length = 262144, CancellationToken ct = default) =>
                ToolResponse.Create(await b.ReadBlobAsync(path, offset, length, ct))),

        ["fs_blob_write"] = new(b => b.DescribeBlobWrite, b =>
            async (string path, string contentBase64, long offset = 0, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default) =>
                ToolResponse.Create(await b.WriteBlobAsync(path, contentBase64, offset, overwrite, createDirectories, ct)))
    };

    public static IMcpServerBuilder AddFileSystemTools<TBackend>(this IMcpServerBuilder builder)
        where TBackend : FileSystemBackendBase
    {
        foreach (var toolName in SupportedToolNames(typeof(TBackend)))
        {
            var wiring = _wiring[toolName];
            builder.Services.AddSingleton<McpServerTool>(sp =>
            {
                var backend = sp.GetRequiredService<TBackend>();
                return McpServerTool.Create(wiring.Handler(backend), new McpServerToolCreateOptions
                {
                    Name = toolName,
                    Description = wiring.Describe(backend)
                });
            });
        }

        return builder;
    }

    // The operations a backend really implements, in the one list's canonical order.
    public static IReadOnlyList<string> SupportedToolNames(Type backendType) =>
        FileSystemOperations.All
            .Where(o => Overrides(backendType, o.MethodName))
            .Select(o => o.ToolName)
            .ToList();

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