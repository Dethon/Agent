using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Infrastructure.Utils;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class FileSystemServerToolsTests
{
    private sealed class RecordingSearchBackend : FileSystemBackendBase
    {
        public override string FilesystemName => "recording";

        public override string DescribeMount => "Records the output mode each search was asked for.";

        public VfsTextSearchOutputMode? SeenMode { get; private set; }

        public override Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path,
            string? directoryPath, string? filePattern, int maxResults, int contextLines,
            VfsTextSearchOutputMode outputMode, CancellationToken ct)
        {
            SeenMode = outputMode;
            return Task.FromResult<FsResult<FsSearchResult>>(new FsResult<FsSearchResult>.Ok(new FsSearchResult
            {
                Query = query,
                Regex = regex,
                Path = path ?? "/",
                FilesSearched = 0,
                FilesWithMatches = 0,
                TotalMatches = 0,
                Truncated = false,
                Results = []
            }));
        }
    }

    [Theory]
    [InlineData("content", VfsTextSearchOutputMode.Content)]
    [InlineData("filesOnly", VfsTextSearchOutputMode.FilesOnly)]
    [InlineData("FILESONLY", VfsTextSearchOutputMode.FilesOnly)]
    [InlineData("Content", VfsTextSearchOutputMode.Content)]
    public async Task Search_KnownOutputMode_DispatchesWithThatMode(string outputMode, VfsTextSearchOutputMode expected)
    {
        var backend = new RecordingSearchBackend();

        var result = await FileSystemServerTools.SearchAsync(
            backend, "q", false, null, null, null, 50, 1, outputMode, CancellationToken.None);

        result.TryGetValue(out _, out _).ShouldBeTrue();
        backend.SeenMode.ShouldBe(expected);
    }

    // An unknown mode used to silently become Content — the caller asked for something the tool
    // does not have, and deserves the invalid-argument envelope, not a quiet default.
    [Fact]
    public async Task Search_UnknownOutputMode_ReturnsInvalidArgumentInsteadOfContent()
    {
        var backend = new RecordingSearchBackend();

        var result = await FileSystemServerTools.SearchAsync(
            backend, "q", false, null, null, null, 50, 1, "banana", CancellationToken.None);

        result.TryGetValue(out _, out var error).ShouldBeFalse();
        error!.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
        error.Message.ShouldContain("banana");
        backend.SeenMode.ShouldBeNull();
    }
}