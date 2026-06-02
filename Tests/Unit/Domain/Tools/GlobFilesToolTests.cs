using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools;

public class GlobFilesToolTests
{
    private const string BasePath = "/library";
    private readonly Mock<IFileSystemClient> _mockClient = new();
    private readonly TestableGlobFilesTool _tool;

    public GlobFilesToolTests()
    {
        _tool = new TestableGlobFilesTool(_mockClient.Object, new LibraryPathConfig(BasePath));
    }

    [Fact]
    public async Task Run_WithMatchingEntries_ReturnsEntryList()
    {
        _mockClient.Setup(c => c.Glob(BasePath, "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["/library/book.pdf", "/library/sub/"]);

        var result = await _tool.TestRun("**/*", CancellationToken.None);

        var array = result["entries"]!.AsArray();
        array.Count.ShouldBe(2);
        array.ShouldContain(n => n!.GetValue<string>() == "sub/");
        result["truncated"]!.GetValue<bool>().ShouldBeFalse();
        FsResultContract.TryValidate("fs_glob", result, out var err).ShouldBeTrue(err);
    }

    [Fact]
    public async Task Run_WithAbsolutePathUnderBasePath_StripsBaseAndUsesRelative()
    {
        _mockClient.Setup(c => c.Glob(BasePath, "docs/**/*.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["/library/docs/book.pdf"]);

        var result = await _tool.TestRun("/library/docs/**/*.pdf", CancellationToken.None);

        result["entries"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Run_WithAbsoluteTrailingSlashPattern_PreservesDirsOnly()
    {
        _mockClient.Setup(c => c.Glob(BasePath, "movies/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["/library/movies/"]);

        var result = await _tool.TestRun("/library/movies/", CancellationToken.None);

        result["entries"]!.AsArray()[0]!.GetValue<string>().ShouldBe("movies/");
        _mockClient.Verify(c => c.Glob(BasePath, "movies/", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task Run_WithEmptyPattern_ThrowsArgumentException(string? pattern)
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _tool.TestRun(pattern!, CancellationToken.None));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("foo/../../bar")]
    [InlineData("..")]
    public async Task Run_WithDotDotPattern_ThrowsArgumentException(string pattern)
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _tool.TestRun(pattern, CancellationToken.None));
    }

    [Fact]
    public async Task Run_WithAbsolutePathOutsideBasePath_ThrowsArgumentException()
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _tool.TestRun("/other/path/**/*.pdf", CancellationToken.None));
    }

    [Fact]
    public async Task Run_OverCap_ReturnsTruncatedObject()
    {
        var entries = Enumerable.Range(1, 250).Select(i => $"/library/file{i}.pdf").ToArray();
        _mockClient.Setup(c => c.Glob(BasePath, "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        var result = await _tool.TestRun("**/*", CancellationToken.None);

        var obj = result.AsObject();
        obj["truncated"]!.GetValue<bool>().ShouldBeTrue();
        obj["total"]!.GetValue<int>().ShouldBe(250);
        obj["entries"]!.AsArray().Count.ShouldBe(200);
        FsResultContract.TryValidate("fs_glob", result, out var err).ShouldBeTrue(err);
    }

    [Fact]
    public async Task Run_WithBasePath_UsesJoinedRoot()
    {
        var expectedRoot = Path.Combine(BasePath, "docs");
        _mockClient.Setup(c => c.Glob(expectedRoot, "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Path.Combine(expectedRoot, "a.txt")]);

        var result = await _tool.TestRun("**/*", "docs", CancellationToken.None);

        result["entries"]!.AsArray().Count.ShouldBe(1);
        _mockClient.Verify(c => c.Glob(BasePath, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("foo/../bar")]
    public async Task Run_WithBasePathContainingDotDot_ThrowsArgumentException(string basePath)
    {
        await Should.ThrowAsync<ArgumentException>(
            () => _tool.TestRun("**/*", basePath, CancellationToken.None));
    }

    private class TestableGlobFilesTool(IFileSystemClient client, LibraryPathConfig libraryPath)
        : GlobFilesTool(client, libraryPath)
    {
        public Task<JsonNode> TestRun(string pattern, CancellationToken cancellationToken)
            => Run(pattern, cancellationToken);

        public Task<JsonNode> TestRun(string pattern, string basePath, CancellationToken cancellationToken)
            => Run(pattern, cancellationToken, basePath);
    }
}