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

    private class TestableGlobFilesTool(IFileSystemClient client, LibraryPathConfig libraryPath)
        : GlobFilesTool(client, libraryPath)
    {
        public Task<JsonNode> TestRun(string pattern, CancellationToken cancellationToken)
            => Run(pattern, cancellationToken);
    }
}
