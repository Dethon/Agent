using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Infrastructure.Agents.Mcp;
using Infrastructure.Utils;
using Shouldly;

namespace Tests.Unit.Domain.Contracts;

// Six places used to enumerate the same twelve operations, and all six had to be edited together
// for a new one to work. They derive from one list now, and these assert that they still do — an
// operation added to the list reaches every surface without a second edit.
public class FileSystemOperationsTests
{
    [Fact]
    public void EveryOperation_NamesABackendMethodThatExists()
    {
        foreach (var operation in FileSystemOperations.All)
        {
            typeof(IFileSystemBackend).GetMethod(operation.MethodName)
                .ShouldNotBeNull($"{operation.ToolName} names no method on the backend contract");
        }
    }

    [Fact]
    public void PayloadTypeTable_IsTheOneList()
    {
        FsResultContract.ResultTypes.Keys.ShouldBe(
            FileSystemOperations.All.Select(o => o.ToolName), ignoreOrder: true);
    }

    [Fact]
    public void SessionFilterSet_IsTheOneList()
    {
        FileSystemOperations.ToolNames.ShouldBe(
            FileSystemOperations.All.Select(o => o.ToolName), ignoreOrder: true);
    }

    [Fact]
    public void CapabilityList_IsTheOneListsModelFacingOperations()
    {
        var everyTool = FileSystemOperations.All.Select(o => o.ToolName);

        McpFileSystemDiscovery.DeriveCapabilities(everyTool).ShouldBe(
            FileSystemOperations.All.Where(o => o.Capability is not null).Select(o => o.Capability!));
    }

    [Fact]
    public void ToolFeatureKeys_AreTheOneListsModelFacingOperations()
    {
        FileSystemToolFeature.AllToolKeys.ShouldBe(
            FileSystemOperations.All.Where(o => o.ToolKey is not null).Select(o => o.ToolKey!),
            ignoreOrder: true);
    }

    // A backend that implements everything advertises everything: the registrar reads the same list.
    [Fact]
    public void Registrar_CanAdvertiseEveryOperationInTheOneList()
    {
        FileSystemServerTools.SupportedToolNames(typeof(EverythingBackend))
            .ShouldBe(FileSystemOperations.All.Select(o => o.ToolName));
    }

    // Validation used to return success for a name it did not recognise, so a typo silently skipped
    // the schema check it was there to perform.
    [Fact]
    public void TryValidate_UnknownToolName_Fails()
    {
        FsResultContract.TryValidate("fs_raed", JsonNode.Parse("{}")!, out var error).ShouldBeFalse();

        error.ShouldNotBeNull().ShouldContain("fs_raed");
    }

    [Fact]
    public void TryValidate_KnownToolNameWithAGoodPayload_Passes()
    {
        var payload = FsResultContract.ToNode(new FsInfoResult { Exists = false, Path = "/x" });

        FsResultContract.TryValidate("fs_info", payload, out var error).ShouldBeTrue(error);
    }

    private sealed class EverythingBackend : FileSystemBackendBase
    {
        public override string FilesystemName => "everything";

        public override Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
            base.ReadAsync(path, offset, limit, ct);

        public override Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) =>
            base.InfoAsync(path, ct);

        public override Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite,
            bool createDirectories, CancellationToken ct) =>
            base.CreateAsync(path, content, overwrite, createDirectories, ct);

        public override Task<FsResult<FsEditResult>> EditAsync(string path,
            IReadOnlyList<global::Domain.DTOs.TextEdit> edits, CancellationToken ct) =>
            base.EditAsync(path, edits, ct);

        public override Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct) =>
            base.GlobAsync(basePath, pattern, ct);

        public override Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path,
            string? directoryPath, string? filePattern, int maxResults, int contextLines,
            global::Domain.DTOs.VfsTextSearchOutputMode outputMode, CancellationToken ct) =>
            base.SearchAsync(query, regex, path, directoryPath, filePattern, maxResults, contextLines, outputMode, ct);

        public override Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
            base.MoveAsync(sourcePath, destinationPath, ct);

        public override Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct) =>
            base.DeleteAsync(path, ct);

        public override Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct) =>
            base.ExecAsync(path, command, timeoutSeconds, ct);

        public override Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath,
            bool overwrite, bool createDirectories, CancellationToken ct) =>
            base.CopyAsync(sourcePath, destinationPath, overwrite, createDirectories, ct);

        public override IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(string path, CancellationToken ct) =>
            base.ReadChunksAsync(path, ct);

        public override Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
            bool overwrite, bool createDirectories, CancellationToken ct) =>
            base.WriteChunksAsync(path, chunks, overwrite, createDirectories, ct);
    }
}