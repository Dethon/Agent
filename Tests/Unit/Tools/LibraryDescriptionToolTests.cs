using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools;
using Moq;
using Shouldly;

namespace Tests.Unit.Tools;

public class LibraryDescriptionToolTests
{
    private const string DefaultLibraryPath = "test/library/path";
    private readonly string[] _defaultDirectoryContents = ["file1.txt", "folder1/", "folder2/file2.txt"];
    private readonly Mock<IFileSystemClient> _mockFileSystemClient = new();

    [Fact]
    public async Task Run_ShouldCallDescribeDirectoryWithCorrectPath()
    {
        // given
        SetupClientMockWithContents();
        var tool = new LibraryDescriptionTool(_mockFileSystemClient.Object, DefaultLibraryPath);

        // when
        await tool.Run(null);

        // then
        _mockFileSystemClient.Verify(c => c.DescribeDirectory(DefaultLibraryPath, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task Run_ShouldReturnSerializedDirectoryDescription()
    {
        // given
        SetupClientMockWithContents();
        var tool = new LibraryDescriptionTool(_mockFileSystemClient.Object, DefaultLibraryPath);

        // when
        var result = await tool.Run(null);

        // then
        result.ShouldNotBeNull();
        var resultArray = result.AsArray();
        resultArray.Count.ShouldBe(3);
        resultArray[0]!.GetValue<string>().ShouldBe("file1.txt");
        resultArray[1]!.GetValue<string>().ShouldBe("folder1/");
        resultArray[2]!.GetValue<string>().ShouldBe("folder2/file2.txt");
    }

    [Fact]
    public void GetToolDefinition_ShouldReturnProperDefinition()
    {
        // given
        var tool = new LibraryDescriptionTool(_mockFileSystemClient.Object, DefaultLibraryPath);

        // when
        var definition = tool.GetToolDefinition();

        // then
        definition.ShouldNotBeNull();
        definition.ShouldBeOfType<ToolDefinition<LibraryDescriptionParams>>();
        definition.Name.ShouldBe("LibraryDescription");
        definition.Description.ShouldBe(
            "Describes the library folder structure to be able to decide where to put downloaded files.");
    }

    [Fact]
    public async Task Run_WithClientFailure_ShouldPropagateException()
    {
        // given
        var tool = new LibraryDescriptionTool(_mockFileSystemClient.Object, DefaultLibraryPath);
        SetupClientFailure("error");

        // when/then
        await Should.ThrowAsync<Exception>(async () => await tool.Run(null));
    }

    #region Helper Methods

    private void SetupClientFailure(string errorMessage)
    {
        _mockFileSystemClient
            .Setup(x => x.DescribeDirectory(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception(errorMessage));
    }

    private void SetupClientMockWithContents(
        string libraryPath = DefaultLibraryPath, string[]? directoryContents = null)
    {
        _mockFileSystemClient.Setup(c => c.DescribeDirectory(libraryPath, CancellationToken.None))
            .ReturnsAsync(directoryContents ?? _defaultDirectoryContents);
    }

    #endregion
}