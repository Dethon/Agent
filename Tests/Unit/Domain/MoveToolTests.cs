using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain;

public class MoveToolTests
{
    private readonly Mock<IFileSystemClient> _clientMock = new();

    private readonly string _libraryPath = OperatingSystem.IsWindows()
        ? @"C:\media\library"
        : "/media/library";

    private TestableMoveToolWrapper CreateTool()
    {
        return new TestableMoveToolWrapper(
            _clientMock.Object,
            new LibraryPathConfig(_libraryPath));
    }

    [Fact]
    public async Task Run_WithAbsolutePaths_Succeeds()
    {
        // Arrange
        var tool = CreateTool();
        var source = Path.Combine(_libraryPath, "movies", "old.mkv");
        var destination = Path.Combine(_libraryPath, "movies", "new.mkv");

        // Act
        var result = await tool.TestRun(source, destination, CancellationToken.None);

        // Assert
        result["status"]!.ToString().ShouldBe("success");
        result["source"]!.ToString().ShouldBe(source);
        result["destination"]!.ToString().ShouldBe(destination);
        _clientMock.Verify(m => m.Move(source, destination, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WithRelativeSourcePath_ResolvesAgainstLibraryRoot()
    {
        // Arrange
        var tool = CreateTool();
        var relativeSource = Path.Combine("movies", "old.mkv");
        var expectedAbsoluteSource = Path.Combine(_libraryPath, "movies", "old.mkv");
        var destination = Path.Combine(_libraryPath, "movies", "new.mkv");

        // Act
        var result = await tool.TestRun(relativeSource, destination, CancellationToken.None);

        // Assert
        result["status"]!.ToString().ShouldBe("success");
        result["source"]!.ToString().ShouldBe(expectedAbsoluteSource);
        _clientMock.Verify(m => m.Move(expectedAbsoluteSource, destination, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WithRelativeDestinationPath_ResolvesAgainstLibraryRoot()
    {
        // Arrange
        var tool = CreateTool();
        var source = Path.Combine(_libraryPath, "movies", "old.mkv");
        var relativeDestination = Path.Combine("movies", "new.mkv");
        var expectedAbsoluteDestination = Path.Combine(_libraryPath, "movies", "new.mkv");

        // Act
        var result = await tool.TestRun(source, relativeDestination, CancellationToken.None);

        // Assert
        result["status"]!.ToString().ShouldBe("success");
        result["destination"]!.ToString().ShouldBe(expectedAbsoluteDestination);
        _clientMock.Verify(
            m => m.Move(source, expectedAbsoluteDestination, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_WithBothRelativePaths_ResolvesAgainstLibraryRoot()
    {
        // Arrange
        var tool = CreateTool();
        var relativeSource = Path.Combine("movies", "old.mkv");
        var relativeDestination = Path.Combine("movies", "new.mkv");
        var expectedAbsoluteSource = Path.Combine(_libraryPath, "movies", "old.mkv");
        var expectedAbsoluteDestination = Path.Combine(_libraryPath, "movies", "new.mkv");

        // Act
        var result = await tool.TestRun(relativeSource, relativeDestination, CancellationToken.None);

        // Assert
        result["status"]!.ToString().ShouldBe("success");
        result["source"]!.ToString().ShouldBe(expectedAbsoluteSource);
        result["destination"]!.ToString().ShouldBe(expectedAbsoluteDestination);
        _clientMock.Verify(
            m => m.Move(expectedAbsoluteSource, expectedAbsoluteDestination, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_WithAbsolutePathOutsideLibrary_ThrowsInvalidOperationException()
    {
        // Arrange
        var tool = CreateTool();
        var outsidePath = OperatingSystem.IsWindows()
            ? @"C:\other\folder\file.txt"
            : "/other/folder/file.txt";
        var validPath = Path.Combine(_libraryPath, "movies", "test.mkv");

        // Act & Assert - source outside
        await Should.ThrowAsync<InvalidOperationException>(
            () => tool.TestRun(outsidePath, validPath, CancellationToken.None));

        // Act & Assert - destination outside
        await Should.ThrowAsync<InvalidOperationException>(
            () => tool.TestRun(validPath, outsidePath, CancellationToken.None));
    }

    [Fact]
    public async Task Run_WithDoubleDotInPath_ThrowsInvalidOperationException()
    {
        // Arrange
        var tool = CreateTool();
        var maliciousPath = Path.Combine(_libraryPath, "..", "etc", "passwd");
        var validPath = Path.Combine(_libraryPath, "movies", "test.mkv");

        // Act & Assert - source with ..
        await Should.ThrowAsync<InvalidOperationException>(
            () => tool.TestRun(maliciousPath, validPath, CancellationToken.None));

        // Act & Assert - destination with ..
        await Should.ThrowAsync<InvalidOperationException>(
            () => tool.TestRun(validPath, maliciousPath, CancellationToken.None));
    }

    [Fact]
    public async Task Run_WithDoubleDotInRelativePath_ThrowsInvalidOperationException()
    {
        // Arrange
        var tool = CreateTool();
        var maliciousRelative = Path.Combine("..", "etc", "passwd");
        var validPath = Path.Combine(_libraryPath, "movies", "test.mkv");

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(
            () => tool.TestRun(maliciousRelative, validPath, CancellationToken.None));
    }

    private class TestableMoveToolWrapper(
        IFileSystemClient client,
        LibraryPathConfig libraryPath)
        : MoveTool(client, libraryPath)
    {
        public Task<JsonNode> TestRun(string sourcePath, string destinationPath, CancellationToken ct)
            => Run(sourcePath, destinationPath, ct);
    }
}
