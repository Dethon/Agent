using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools;
using Moq;
using Shouldly;

namespace Tests.Unit.Tools;

public class MoveTests
{
    private const string DefaultLibraryPath = "test/library/path";
    private readonly Mock<IFileSystemClient> _mockFileSystemClient = new();

    [Fact]
    public async Task Run_ShouldCallMoveWithCorrectParameters()
    {
        // given
        var tool = new MoveTool(_mockFileSystemClient.Object, DefaultLibraryPath);
        const string sourcePath = $"{DefaultLibraryPath}/source.txt";
        const string destinationPath = $"{DefaultLibraryPath}/destination.txt";
        var parameters = new JsonObject
        {
            ["SourcePath"] = sourcePath,
            ["DestinationPath"] = destinationPath
        };

        // when
        await tool.Run(parameters);

        // then
        _mockFileSystemClient.Verify(c =>
            c.Move(sourcePath, destinationPath, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task Run_ShouldReturnSuccessResponse()
    {
        // given
        var tool = new MoveTool(_mockFileSystemClient.Object, DefaultLibraryPath);
        const string sourcePath = $"{DefaultLibraryPath}/source.txt";
        const string destinationPath = $"{DefaultLibraryPath}/destination.txt";
        var parameters = new JsonObject
        {
            ["SourcePath"] = sourcePath,
            ["DestinationPath"] = destinationPath
        };

        // when
        var result = await tool.Run(parameters);

        // then
        result.ShouldNotBeNull();
        result["status"]?.GetValue<string>().ShouldBe("success");
        result["message"]?.GetValue<string>().ShouldBe("File moved successfully");
        result["source"]?.GetValue<string>().ShouldBe(sourcePath);
        result["destination"]?.GetValue<string>().ShouldBe(destinationPath);
    }

    [Fact]
    public async Task Run_ShouldThrowArgumentException_WhenSourcePathDoesNotStartWithLibraryPath()
    {
        // given
        var tool = new MoveTool(_mockFileSystemClient.Object, DefaultLibraryPath);
        const string sourcePath = "/invalid/source.txt";
        const string destinationPath = $"{DefaultLibraryPath}/destination.txt";
        var parameters = new JsonObject
        {
            ["SourcePath"] = sourcePath,
            ["DestinationPath"] = destinationPath
        };

        // when/then
        await Should.ThrowAsync<ArgumentException>(() => tool.Run(parameters));
    }

    [Fact]
    public async Task Run_ShouldThrowArgumentException_WhenDestinationPathDoesNotStartWithLibraryPath()
    {
        // given
        var tool = new MoveTool(_mockFileSystemClient.Object, DefaultLibraryPath);
        const string sourcePath = $"{DefaultLibraryPath}/source.txt";
        const string destinationPath = "/invalid/destination.txt";
        var parameters = new JsonObject
        {
            ["SourcePath"] = sourcePath,
            ["DestinationPath"] = destinationPath
        };

        // when/then
        await Should.ThrowAsync<ArgumentException>(() => tool.Run(parameters));
    }

    [Fact]
    public void GetToolDefinition_ShouldReturnProperDefinition()
    {
        // given
        var tool = new MoveTool(_mockFileSystemClient.Object, DefaultLibraryPath);

        // when
        var definition = tool.GetToolDefinition();

        // then
        definition.ShouldNotBeNull();
        definition.ShouldBeOfType<ToolDefinition<FileMoveParams>>();
        definition.Name.ShouldBe("Move");
        definition.Description.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Run_WithClientFailure_ShouldPropagateException()
    {
        // given
        var tool = new MoveTool(_mockFileSystemClient.Object, DefaultLibraryPath);
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

    #endregion
}