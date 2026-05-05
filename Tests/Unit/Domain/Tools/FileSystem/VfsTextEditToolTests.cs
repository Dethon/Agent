using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class TextEditToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsTextEditTool _tool;

    public TextEditToolTests()
    {
        _tool = new VfsTextEditTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndForwardsEdits()
    {
        var expected = new JsonObject { ["status"] = "success", ["totalOccurrencesReplaced"] = 1 };
        var edits = new[] { new TextEdit("old", "new") };
        _registry.Setup(r => r.Resolve("/library/file.md"))
            .Returns(new FileSystemResolution(_backend.Object, "file.md"));
        _backend.Setup(b => b.EditAsync(
                "file.md",
                It.Is<IReadOnlyList<TextEdit>>(list =>
                    list.Count == 1 &&
                    list[0].OldString == "old" &&
                    list[0].NewString == "new" &&
                    list[0].ReplaceAll == false),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/library/file.md", edits, CancellationToken.None);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_ForwardsMultipleEditsInOrder()
    {
        var expected = new JsonObject { ["status"] = "success", ["totalOccurrencesReplaced"] = 5 };
        var edits = new[]
        {
            new TextEdit("a", "A", ReplaceAll: true),
            new TextEdit("b", "B")
        };
        _registry.Setup(r => r.Resolve("/vault/config.md"))
            .Returns(new FileSystemResolution(_backend.Object, "config.md"));
        _backend.Setup(b => b.EditAsync(
                "config.md",
                It.Is<IReadOnlyList<TextEdit>>(list =>
                    list.Count == 2 &&
                    list[0].OldString == "a" && list[0].ReplaceAll == true &&
                    list[1].OldString == "b" && list[1].ReplaceAll == false),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/vault/config.md", edits, CancellationToken.None);

        result.ShouldBe(expected);
    }
}
