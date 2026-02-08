using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Moq;
using Shouldly;

namespace Tests.Unit.Tools;

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
    public async Task Run_WithValidPattern_ReturnsJsonArray()
    {
        // Arrange
        _mockClient.Setup(c => c.GlobFiles(BasePath, "**/*.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["/library/book.pdf", "/library/sub/doc.pdf"]);

        // Act
        var result = await _tool.TestRun("**/*.pdf", CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        var array = result.AsArray();
        array.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task Run_WithEmptyPattern_ThrowsArgumentException(string? pattern)
    {
        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(
            () => _tool.TestRun(pattern!, CancellationToken.None));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("foo/../../bar")]
    [InlineData("..")]
    public async Task Run_WithDotDotPattern_ThrowsArgumentException(string pattern)
    {
        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(
            () => _tool.TestRun(pattern, CancellationToken.None));
    }

    [Fact]
    public async Task Run_WithAbsolutePathUnderBasePath_StripsBaseAndUsesRelative()
    {
        // Arrange
        _mockClient.Setup(c => c.GlobFiles(BasePath, "docs/**/*.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["/library/docs/book.pdf"]);

        // Act
        var result = await _tool.TestRun("/library/docs/**/*.pdf", GlobMode.Files, CancellationToken.None);

        // Assert
        result.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Run_WithAbsolutePathOutsideBasePath_ThrowsArgumentException()
    {
        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(
            () => _tool.TestRun("/other/path/**/*.pdf", GlobMode.Files, CancellationToken.None));
    }

    [Fact]
    public async Task Run_DirectoriesMode_CallsGlobDirectoriesAndReturnsArray()
    {
        // Arrange
        _mockClient.Setup(c => c.GlobDirectories(BasePath, "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["/library/movies", "/library/books"]);

        // Act
        var result = await _tool.TestRun("**/*", GlobMode.Directories, CancellationToken.None);

        // Assert
        var array = result.AsArray();
        array.Count.ShouldBe(2);
        array[0]!.GetValue<string>().ShouldBe("/library/movies");
    }

    [Fact]
    public async Task Run_FilesMode_UnderCap_ReturnsPlainArray()
    {
        // Arrange
        _mockClient.Setup(c => c.GlobFiles(BasePath, "**/*.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["/library/a.pdf", "/library/b.pdf"]);

        // Act
        var result = await _tool.TestRun("**/*.pdf", GlobMode.Files, CancellationToken.None);

        // Assert
        var array = result.AsArray();
        array.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Run_FilesMode_OverCap_ReturnsTruncatedObject()
    {
        // Arrange
        var files = Enumerable.Range(1, 250).Select(i => $"/library/file{i}.pdf").ToArray();
        _mockClient.Setup(c => c.GlobFiles(BasePath, "**/*.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);

        // Act
        var result = await _tool.TestRun("**/*.pdf", GlobMode.Files, CancellationToken.None);

        // Assert
        var obj = result.AsObject();
        obj["truncated"]!.GetValue<bool>().ShouldBeTrue();
        obj["total"]!.GetValue<int>().ShouldBe(250);
        obj["files"]!.AsArray().Count.ShouldBe(200);
        obj["message"]!.GetValue<string>().ShouldContain("200");
        obj["message"]!.GetValue<string>().ShouldContain("250");
    }

    private class TestableGlobFilesTool(IFileSystemClient client, LibraryPathConfig libraryPath)
        : GlobFilesTool(client, libraryPath)
    {
        public Task<JsonNode> TestRun(string pattern, CancellationToken cancellationToken)
            => Run(pattern, GlobMode.Files, cancellationToken);

        public Task<JsonNode> TestRun(string pattern, GlobMode mode, CancellationToken cancellationToken)
            => Run(pattern, mode, cancellationToken);
    }
}
