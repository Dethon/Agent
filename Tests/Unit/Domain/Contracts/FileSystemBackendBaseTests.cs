using System.Text.RegularExpressions;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Shouldly;

namespace Tests.Unit.Domain.Contracts;

public class FileSystemBackendBaseTests
{
    private sealed class BareBackend : FileSystemBackendBase
    {
        public override string FilesystemName => "bare";

        public override string DescribeMount => "A bare mount that implements nothing.";

        public (bool DirsOnly, Func<string, bool> Matches) Prologue(string? basePath, string pattern) =>
            GlobPrologue(basePath, pattern);

        public FsResult<Regex> Compile(string query, bool regex) =>
            CompileSearchRegex(query, regex);

        public Task<FsResult<FsSearchResult>> Scan(
            IEnumerable<string> nodes, FsSearchScan scan, Func<string, string?> content) =>
            SearchNodesAsync(nodes, (n, _) => ValueTask.FromResult(($"/{n}", content(n))), scan, CancellationToken.None);
    }

    private static readonly BareBackend _bare = new();

    public static TheoryData<string, Func<FileSystemBackendBase, Task<ToolErrorResult?>>> Operations() => new()
    {
        { VfsTextReadTool.Name, async b => ErrorOf(await b.ReadAsync("/x", null, null, default)) },
        { VfsFileInfoTool.Name, async b => ErrorOf(await b.InfoAsync("/x", default)) },
        { VfsTextCreateTool.Name, async b => ErrorOf(await b.CreateAsync("/x", "c", false, true, default)) },
        { VfsTextEditTool.Name, async b => ErrorOf(await b.EditAsync("/x", [], default)) },
        { VfsGlobFilesTool.Name, async b => ErrorOf(await b.GlobAsync("/", "*", default)) },
        {
            VfsTextSearchTool.Name,
            async b => ErrorOf(await b.SearchAsync("q", false, null, null, null, 50, 1, VfsTextSearchOutputMode.Content, default))
        },
        { VfsMoveTool.Name, async b => ErrorOf(await b.MoveAsync("/a", "/b", default)) },
        { VfsRemoveTool.Name, async b => ErrorOf(await b.DeleteAsync("/x", default)) },
        { VfsExecTool.Name, async b => ErrorOf(await b.ExecAsync("/", "c", null, default)) },
        { VfsCopyTool.Name, async b => ErrorOf(await b.CopyAsync("/a", "/b", false, true, default)) }
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Operation_NotOverridden_ReturnsUnsupportedNamingTheOperation(
        string operation, Func<FileSystemBackendBase, Task<ToolErrorResult?>> call)
    {
        var error = await call(_bare);

        error.ShouldNotBeNull();
        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Message.ShouldContain(operation);
        error.Message.ShouldContain("bare");
    }

    [Fact]
    public async Task Unsupported_NamesTheOperationNotTheMethod()
    {
        var error = ErrorOf(await _bare.CreateAsync("/x", "c", false, true, default));

        error!.Message.ShouldNotContain("CreateAsync");
        error.Message.ShouldContain("text_create");
    }

    [Fact]
    public async Task BlobOperations_NotOverridden_ReportUnsupported()
    {
        await Should.ThrowAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in _bare.ReadChunksAsync("/x", default))
            {
            }
        });

        await Should.ThrowAsync<NotSupportedException>(
            () => _bare.WriteChunksAsync("/x", AsyncEmpty(), false, true, default));
    }

    [Fact]
    public void GlobPrologue_BasePath_ScopesThePattern()
    {
        var (dirsOnly, matches) = _bare.Prologue("/docs", "*.md");

        dirsOnly.ShouldBeFalse();
        matches("docs/notes.md").ShouldBeTrue();
        matches("other/notes.md").ShouldBeFalse();
    }

    [Fact]
    public void GlobPrologue_TrailingSlash_AsksForDirectoriesOnly()
    {
        var (dirsOnly, matches) = _bare.Prologue(null, "*/");

        dirsOnly.ShouldBeTrue();
        matches("docs").ShouldBeTrue();
    }

    [Fact]
    public void CompileSearchRegex_LiteralQuery_MatchesCaseInsensitively()
    {
        var compiled = _bare.Compile("a.b", regex: false);

        compiled.TryGetValue(out var matcher, out _).ShouldBeTrue();
        matcher!.IsMatch("A.B").ShouldBeTrue();
        matcher.IsMatch("axb").ShouldBeFalse();
        matcher.MatchTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void CompileSearchRegex_UncompilablePattern_ReturnsInvalidArgument()
    {
        var compiled = _bare.Compile("[unclosed", regex: true);

        compiled.TryGetValue(out _, out var error).ShouldBeFalse();
        error!.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
    }

    [Fact]
    public async Task SearchNodesAsync_CountsFilesAndMatches()
    {
        var result = await _bare.Scan(
            ["a", "b", "c"],
            Scan("needle"),
            n => n == "c" ? null : $"line one\nneedle in {n}\nlast");

        result.TryGetValue(out var value, out _).ShouldBeTrue();
        value!.FilesSearched.ShouldBe(2);
        value.FilesWithMatches.ShouldBe(2);
        value.TotalMatches.ShouldBe(2);
        value.Truncated.ShouldBeFalse();
        value.Results.Select(r => r.File).ShouldBe(["/a", "/b"]);
    }

    [Fact]
    public async Task SearchNodesAsync_MaxResultsReached_ReportsTruncated()
    {
        var result = await _bare.Scan(
            ["a", "b"],
            Scan("needle") with { MaxResults = 1 },
            _ => "needle\nneedle");

        result.TryGetValue(out var value, out _).ShouldBeTrue();
        value!.TotalMatches.ShouldBe(1);
        value.Truncated.ShouldBeTrue();
    }

    [Fact]
    public async Task SearchNodesAsync_UncompilablePattern_ReturnsInvalidArgument()
    {
        var result = await _bare.Scan(["a"], Scan("[unclosed") with { Regex = true }, _ => "text");

        result.TryGetValue(out _, out var error).ShouldBeFalse();
        error!.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
    }

    [Fact]
    public void Descriptions_HaveGenericDefaults()
    {
        _bare.DescribeRead.ShouldNotBeNullOrWhiteSpace();
        _bare.DescribeInfo.ShouldNotBeNullOrWhiteSpace();
        _bare.DescribeCreate.ShouldNotBeNullOrWhiteSpace();
        _bare.DescribeEdit.ShouldNotBeNullOrWhiteSpace();
        _bare.DescribeGlob.ShouldNotBeNullOrWhiteSpace();
        _bare.DescribeSearch.ShouldNotBeNullOrWhiteSpace();
        _bare.DescribeMove.ShouldNotBeNullOrWhiteSpace();
        _bare.DescribeDelete.ShouldNotBeNullOrWhiteSpace();
        _bare.DescribeExec.ShouldNotBeNullOrWhiteSpace();
        _bare.DescribeCopy.ShouldNotBeNullOrWhiteSpace();
        _bare.DescribeBlobRead.ShouldNotBeNullOrWhiteSpace();
        _bare.DescribeBlobWrite.ShouldNotBeNullOrWhiteSpace();
    }

    private static FsSearchScan Scan(string query) => new()
    {
        Query = query,
        Regex = false,
        Path = "/",
        MaxResults = 50,
        ContextLines = 0,
        OutputMode = VfsTextSearchOutputMode.Content
    };

    private static ToolErrorResult? ErrorOf<T>(FsResult<T> result) where T : class =>
        result is FsResult<T>.Err err ? err.Error : null;

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> AsyncEmpty()
    {
        await Task.CompletedTask;
        yield break;
    }
}