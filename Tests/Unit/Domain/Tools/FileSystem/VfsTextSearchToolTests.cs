using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class TextSearchToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsTextSearchTool _tool;

    public TextSearchToolTests()
    {
        _tool = new VfsTextSearchTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_DirectorySearch_ResolvesAndCallsBackend()
    {
        _registry.Setup(r => r.Resolve("/library"))
            .Returns(new FileSystemResolution(_backend.Object, ""));
        _backend.Setup(b => b.SearchAsync("kubernetes", false, null, "", null, 50, 1, VfsTextSearchOutputMode.Content, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsSearchResult>.Ok(new FsSearchResult
            {
                Query = "kubernetes", Regex = false, Path = "", FilesSearched = 3,
                FilesWithMatches = 2, TotalMatches = 5, Truncated = false, Results = []
            }));

        var result = await _tool.RunAsync("kubernetes", directoryPath: "/library", cancellationToken: CancellationToken.None);

        result!["totalMatches"]!.GetValue<int>().ShouldBe(5);
    }

    [Fact]
    public async Task RunAsync_SingleFileSearch_ResolvesFilePath()
    {
        _registry.Setup(r => r.Resolve("/vault/notes/todo.md"))
            .Returns(new FileSystemResolution(_backend.Object, "notes/todo.md"));
        _backend.Setup(b => b.SearchAsync("TODO", false, "notes/todo.md", null, null, 50, 1, VfsTextSearchOutputMode.Content, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsSearchResult>.Ok(new FsSearchResult
            {
                Query = "TODO", Regex = false, Path = "notes/todo.md", FilesSearched = 1,
                FilesWithMatches = 1, TotalMatches = 1, Truncated = false, Results = []
            }));

        var result = await _tool.RunAsync("TODO", filePath: "/vault/notes/todo.md", cancellationToken: CancellationToken.None);

        result!["totalMatches"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_NeitherFilePathNorDirectoryPath_ReturnsInvalidArgumentError()
    {
        var result = await _tool.RunAsync("query", cancellationToken: CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        result["message"]!.GetValue<string>().ShouldContain("filePath or directoryPath");
    }
}