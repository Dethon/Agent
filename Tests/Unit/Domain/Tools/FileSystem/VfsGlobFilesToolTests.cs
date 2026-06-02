using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsGlobFilesToolTests
{
    private static VfsGlobFilesTool Build(string mountPoint, string relativePath, FsGlobResult backendResult,
        out Mock<IFileSystemBackend> backend)
    {
        backend = new Mock<IFileSystemBackend>();
        backend.Setup(b => b.GlobAsync(relativePath, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsGlobResult>.Ok(backendResult));

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve(It.IsAny<string>()))
            .Returns(new FileSystemResolution(backend.Object, relativePath, mountPoint));

        return new VfsGlobFilesTool(registry.Object);
    }

    private static IReadOnlyList<string> Entries(System.Text.Json.Nodes.JsonNode node) =>
        node["entries"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

    [Fact]
    public async Task Run_PrependsMountPoint_ToMountRelativeEntries()
    {
        var tool = Build("/ha", "entities", new FsGlobResult
        {
            Entries = ["entities/light/kitchen/", "entities/light/kitchen/state.json"],
            Truncated = false,
            Total = 2
        }, out _);

        var result = await tool.RunAsync("/ha/entities", "**", CancellationToken.None);

        Entries(result).ShouldBe(["/ha/entities/light/kitchen/", "/ha/entities/light/kitchen/state.json"]);
    }

    [Fact]
    public async Task Run_NormalizesLeadingSlashEntries_WithoutDoubleSlash()
    {
        var tool = Build("/print-queue", "", new FsGlobResult
        {
            Entries = ["/note.txt", "/status.json"],
            Truncated = false,
            Total = 2
        }, out _);

        var result = await tool.RunAsync("/print-queue", "*", CancellationToken.None);

        Entries(result).ShouldBe(["/print-queue/note.txt", "/print-queue/status.json"]);
    }

    [Fact]
    public async Task Run_PreservesDirectoryTrailingSlash()
    {
        var tool = Build("/schedules", "jonas", new FsGlobResult
        {
            Entries = ["/jonas/morning-news/"],
            Truncated = false,
            Total = 1
        }, out _);

        var result = await tool.RunAsync("/schedules/jonas", "*/", CancellationToken.None);

        Entries(result).ShouldBe(["/schedules/jonas/morning-news/"]);
    }
}