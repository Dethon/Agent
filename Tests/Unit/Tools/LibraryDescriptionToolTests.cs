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
    public void Name_ShouldReturnCorrectValue()
    {
        // given
        var tool = CreateTool(_mockFileSystemClient);

        // when
        var name = tool.Name;

        // then
        name.ShouldBe("LibraryDescription");
    }

    [Fact]
    public async Task Run_ShouldCallDescribeDirectoryWithCorrectPath()
    {
        // given
        SetupClientMockWithContents();
        var tool = CreateTool(_mockFileSystemClient);

        // when
        await tool.Run(null);

        // then
        _mockFileSystemClient.Verify(c => c.DescribeDirectory(DefaultLibraryPath), Times.Once);
    }

    [Fact]
    public async Task Run_ShouldReturnSerializedDirectoryDescription()
    {
        // given
        SetupClientMockWithContents();
        var tool = CreateTool(_mockFileSystemClient);

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
        var tool = CreateTool(_mockFileSystemClient);

        // when
        var definition = tool.GetToolDefinition();

        // then
        definition.ShouldNotBeNull();
        definition.ShouldBeOfType<ToolDefinition<LibraryDescriptionParams>>();
        definition.Name.ShouldBe("LibraryDescription");
        definition.Description.ShouldBe(
            "Describes the library folder structure to be able to decide where to put downloaded files.");
    }

    #region Helper Methods

    private static LibraryDescriptionTool CreateTool(
        Mock<IFileSystemClient> clientMock,
        string libraryPath = DefaultLibraryPath)
    {
        return new LibraryDescriptionTool(clientMock.Object, libraryPath);
    }

    private void SetupClientMockWithContents(
        string libraryPath = DefaultLibraryPath, string[]? directoryContents = null)
    {
        _mockFileSystemClient.Setup(c => c.DescribeDirectory(libraryPath))
            .ReturnsAsync(directoryContents ?? _defaultDirectoryContents);
    }

    #endregion
}