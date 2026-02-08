using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Clients;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain;

public class RemoveFileToolTests
{
    private readonly Mock<IFileSystemClient> _fileSystemClientMock = new();

    private readonly string _libraryPath = OperatingSystem.IsWindows()
        ? @"C:\media\library"
        : "/media/library";

    private TestableRemoveFileTool CreateTool()
    {
        return new TestableRemoveFileTool(
            _fileSystemClientMock.Object,
            new LibraryPathConfig(_libraryPath));
    }

    [Fact]
    public async Task Run_WithValidPath_MovesToTrash()
    {
        // Arrange
        var tool = CreateTool();
        var filePath = Path.Combine(_libraryPath, "movies", "test.mkv");
        var trashPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            LocalFileSystemClient.TrashFolderName,
            "test.mkv");

        _fileSystemClientMock
            .Setup(m => m.MoveToTrash(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(trashPath);

        // Act
        var result = await tool.TestRun(filePath, CancellationToken.None);

        // Assert
        result["status"]!.ToString().ShouldBe("success");
        result["message"]!.ToString().ShouldBe("File moved to trash");
        result["originalPath"]!.ToString().ShouldBe(filePath);
        result["trashPath"]!.ToString().ShouldBe(trashPath);
        _fileSystemClientMock.Verify(m => m.MoveToTrash(filePath, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WithPathContainingDoubleDot_ThrowsInvalidOperationException()
    {
        // Arrange
        var tool = CreateTool();
        var maliciousPath = Path.Combine(_libraryPath, "..", "etc", "passwd");

        // Act & Assert
        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await tool.TestRun(maliciousPath, CancellationToken.None));

        exception.Message.ShouldContain("must not contain '..'");
        _fileSystemClientMock.Verify(m => m.MoveToTrash(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_WithPathOutsideLibrary_ThrowsInvalidOperationException()
    {
        // Arrange
        var tool = CreateTool();
        var outsidePath = OperatingSystem.IsWindows()
            ? @"C:\other\folder\file.txt"
            : "/other/folder/file.txt";

        // Act & Assert
        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await tool.TestRun(outsidePath, CancellationToken.None));

        exception.Message.ShouldContain("must be within the library");
        _fileSystemClientMock.Verify(m => m.MoveToTrash(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_WithRelativePath_ResolvesAgainstLibraryRoot()
    {
        // Arrange
        var tool = CreateTool();
        var relativePath = Path.Combine("movies", "test.mkv");
        var expectedAbsolutePath = Path.Combine(_libraryPath, "movies", "test.mkv");
        const string trashPath = "trash-path";

        _fileSystemClientMock
            .Setup(m => m.MoveToTrash(expectedAbsolutePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(trashPath);

        // Act
        var result = await tool.TestRun(relativePath, CancellationToken.None);

        // Assert
        result["status"]!.ToString().ShouldBe("success");
        result["originalPath"]!.ToString().ShouldBe(expectedAbsolutePath);
        _fileSystemClientMock.Verify(
            m => m.MoveToTrash(expectedAbsolutePath, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WithDoubleDotInRelativePath_ThrowsInvalidOperationException()
    {
        // Arrange
        var tool = CreateTool();
        var maliciousRelative = Path.Combine("..", "etc", "passwd");

        // Act & Assert
        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await tool.TestRun(maliciousRelative, CancellationToken.None));

        exception.Message.ShouldContain("must not contain '..'");
    }

    [Fact]
    public async Task Run_WithNestedValidPath_Succeeds()
    {
        // Arrange
        var tool = CreateTool();
        var filePath = Path.Combine(_libraryPath, "movies", "action", "2024", "movie.mkv");
        const string trashPath = "trash-path";

        _fileSystemClientMock
            .Setup(m => m.MoveToTrash(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(trashPath);

        // Act
        var result = await tool.TestRun(filePath, CancellationToken.None);

        // Assert
        result["status"]!.ToString().ShouldBe("success");
        _fileSystemClientMock.Verify(m => m.MoveToTrash(filePath, It.IsAny<CancellationToken>()), Times.Once);
    }

    private class TestableRemoveFileTool(
        IFileSystemClient client,
        LibraryPathConfig libraryPath)
        : RemoveFileTool(client, libraryPath)
    {
        public Task<JsonNode> TestRun(string path, CancellationToken ct)
        {
            return Run(path, ct);
        }
    }
}