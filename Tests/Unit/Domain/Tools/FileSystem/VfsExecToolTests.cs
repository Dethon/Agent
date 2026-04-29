using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsExecToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsExecTool _tool;

    public VfsExecToolTests()
    {
        _tool = new VfsExecTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndCallsBackend()
    {
        var expected = new JsonObject
        {
            ["stdout"] = "hi\n",
            ["stderr"] = "",
            ["exitCode"] = 0,
            ["timedOut"] = false,
            ["truncated"] = false
        };
        _registry.Setup(r => r.Resolve("/sandbox/home/sandbox_user"))
            .Returns(new FileSystemResolution(_backend.Object, "home/sandbox_user"));
        _backend.Setup(b => b.ExecAsync("home/sandbox_user", "echo hi", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/sandbox/home/sandbox_user", "echo hi");

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_PassesTimeoutSecondsThrough()
    {
        var expected = new JsonObject { ["exitCode"] = 0 };
        _registry.Setup(r => r.Resolve("/sandbox"))
            .Returns(new FileSystemResolution(_backend.Object, ""));
        _backend.Setup(b => b.ExecAsync("", "sleep 1", 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/sandbox", "sleep 1", timeoutSeconds: 30);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_RootPath_PassesEmptyRelativePath()
    {
        var expected = new JsonObject { ["exitCode"] = 0 };
        _registry.Setup(r => r.Resolve("/sandbox"))
            .Returns(new FileSystemResolution(_backend.Object, ""));
        _backend.Setup(b => b.ExecAsync("", "pwd", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        await _tool.RunAsync("/sandbox", "pwd");

        _backend.Verify(b => b.ExecAsync("", "pwd", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_UnknownMount_ThrowsFromRegistry()
    {
        _registry.Setup(r => r.Resolve("/unknown"))
            .Throws(new InvalidOperationException("No filesystem mounted"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _tool.RunAsync("/unknown", "echo hi"));
    }
}
