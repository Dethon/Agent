using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;

namespace Domain.Contracts;

// One operation on a filesystem backend, named once in every vocabulary that has to talk about it:
// the backend method that implements it, the MCP tool a server advertises for it, the result type
// its payload deserialises to, and — for the ten the model can call directly — the domain tool's
// key and leaf name.
public sealed record FileSystemOperation
{
    public required string ToolName { get; init; }
    public required string MethodName { get; init; }
    public required Type ResultType { get; init; }

    // Null for the two byte-streaming operations: they are the agent's transfer machinery, not
    // something the model calls, so they have no domain tool and appear in no capability list.
    public string? ToolKey { get; init; }
    public string? Capability { get; init; }
}

// The one list. The registrar, the payload-type table, the capability map, the session's filter set
// and the tool feature all derive from it, so a new operation cannot half-exist because one of them
// was missed. Order is the canonical display order for a mount's capability list.
public static class FileSystemOperations
{
    public static readonly IReadOnlyList<FileSystemOperation> All =
    [
        Op("fs_read", nameof(IFileSystemBackend.ReadAsync), typeof(FsReadResult),
            VfsTextReadTool.Key, VfsTextReadTool.Name),
        Op("fs_create", nameof(IFileSystemBackend.CreateAsync), typeof(FsCreateResult),
            VfsTextCreateTool.Key, VfsTextCreateTool.Name),
        Op("fs_edit", nameof(IFileSystemBackend.EditAsync), typeof(FsEditResult),
            VfsTextEditTool.Key, VfsTextEditTool.Name),
        Op("fs_glob", nameof(IFileSystemBackend.GlobAsync), typeof(FsGlobResult),
            VfsGlobFilesTool.Key, VfsGlobFilesTool.Name),
        Op("fs_search", nameof(IFileSystemBackend.SearchAsync), typeof(FsSearchResult),
            VfsTextSearchTool.Key, VfsTextSearchTool.Name),
        Op("fs_move", nameof(IFileSystemBackend.MoveAsync), typeof(FsMoveResult),
            VfsMoveTool.Key, VfsMoveTool.Name),
        Op("fs_copy", nameof(IFileSystemBackend.CopyAsync), typeof(FsCopyResult),
            VfsCopyTool.Key, VfsCopyTool.Name),
        Op("fs_delete", nameof(IFileSystemBackend.DeleteAsync), typeof(FsRemoveResult),
            VfsRemoveTool.Key, VfsRemoveTool.Name),
        Op("fs_info", nameof(IFileSystemBackend.InfoAsync), typeof(FsInfoResult),
            VfsFileInfoTool.Key, VfsFileInfoTool.Name),
        Op("fs_exec", nameof(IFileSystemBackend.ExecAsync), typeof(FsExecResult),
            VfsExecTool.Key, VfsExecTool.Name),
        Op("fs_blob_read", nameof(IFileSystemBackend.ReadChunksAsync), typeof(FsBlobReadResult)),
        Op("fs_blob_write", nameof(IFileSystemBackend.WriteChunksAsync), typeof(FsBlobWriteResult))
    ];

    public static readonly IReadOnlySet<string> ToolNames =
        All.Select(o => o.ToolName).ToHashSet(StringComparer.Ordinal);

    private static FileSystemOperation Op(
        string toolName, string methodName, Type resultType, string? key = null, string? capability = null) =>
        new()
        {
            ToolName = toolName,
            MethodName = methodName,
            ResultType = resultType,
            ToolKey = key,
            Capability = capability
        };
}