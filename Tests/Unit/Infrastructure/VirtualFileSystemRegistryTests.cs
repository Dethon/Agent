using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class VirtualFileSystemRegistryTests
{
    private readonly VirtualFileSystemRegistry _registry = new();

    [Fact]
    public async Task DiscoverAsync_RegistersMountsFromFactory()
    {
        var backend = CreateMockBackend("library");
        var factory = CreateMockFactory("http://mcp-text:8080/mcp",
            (new FileSystemMount("library", "/library", "Personal document library"), backend));

        await _registry.DiscoverAsync(["http://mcp-text:8080/mcp"], factory, CancellationToken.None);

        var mounts = _registry.GetMounts();
        mounts.Count.ShouldBe(1);
        mounts[0].Name.ShouldBe("library");
        mounts[0].MountPoint.ShouldBe("/library");
    }

    [Fact]
    public async Task DiscoverAsync_MultipleEndpoints_RegistersAll()
    {
        var libraryBackend = CreateMockBackend("library");
        var vaultBackend = CreateMockBackend("vault");

        var factory = new Mock<IFileSystemBackendFactory>();
        factory.Setup(f => f.DiscoverAsync("http://mcp-text:8080/mcp", It.IsAny<CancellationToken>()))
            .ReturnsAsync([(new FileSystemMount("library", "/library", "Library"), libraryBackend)]);
        factory.Setup(f => f.DiscoverAsync("http://mcp-vault:8080/mcp", It.IsAny<CancellationToken>()))
            .ReturnsAsync([(new FileSystemMount("vault", "/vault", "Vault"), vaultBackend)]);

        await _registry.DiscoverAsync(
            ["http://mcp-text:8080/mcp", "http://mcp-vault:8080/mcp"],
            factory.Object, CancellationToken.None);

        _registry.GetMounts().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Resolve_MatchingMount_ReturnsBackendAndRelativePath()
    {
        var backend = CreateMockBackend("library");
        var factory = CreateMockFactory("http://mcp-text:8080/mcp",
            (new FileSystemMount("library", "/library", "Library"), backend));

        await _registry.DiscoverAsync(["http://mcp-text:8080/mcp"], factory, CancellationToken.None);

        var resolution = _registry.Resolve("/library/notes/todo.md");
        resolution.Backend.ShouldBe(backend);
        resolution.RelativePath.ShouldBe("notes/todo.md");
    }

    [Fact]
    public async Task Resolve_RootPath_ReturnsEmptyRelativePath()
    {
        var backend = CreateMockBackend("library");
        var factory = CreateMockFactory("http://mcp-text:8080/mcp",
            (new FileSystemMount("library", "/library", "Library"), backend));

        await _registry.DiscoverAsync(["http://mcp-text:8080/mcp"], factory, CancellationToken.None);

        var resolution = _registry.Resolve("/library");
        resolution.Backend.ShouldBe(backend);
        resolution.RelativePath.ShouldBe("");
    }

    [Fact]
    public async Task Resolve_LongestPrefixWins()
    {
        var libraryBackend = CreateMockBackend("library");
        var docsBackend = CreateMockBackend("docs");

        var factory = new Mock<IFileSystemBackendFactory>();
        factory.Setup(f => f.DiscoverAsync("http://ep1:8080/mcp", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                (new FileSystemMount("library", "/library", "Library"), libraryBackend),
                (new FileSystemMount("docs", "/library/docs", "Docs"), docsBackend)
            ]);

        await _registry.DiscoverAsync(["http://ep1:8080/mcp"], factory.Object, CancellationToken.None);

        var resolution = _registry.Resolve("/library/docs/readme.md");
        resolution.Backend.ShouldBe(docsBackend);
        resolution.RelativePath.ShouldBe("readme.md");
    }

    [Fact]
    public void Resolve_NoMatchingMount_ThrowsWithAvailableMounts()
    {
        var ex = Should.Throw<InvalidOperationException>(() => _registry.Resolve("/unknown/file.md"));
        ex.Message.ShouldContain("No filesystem mounted");
    }

    [Fact]
    public async Task Resolve_NoMatchingMount_ErrorListsAvailable()
    {
        var backend = CreateMockBackend("library");
        var factory = CreateMockFactory("http://ep:8080/mcp",
            (new FileSystemMount("library", "/library", "Library"), backend));

        await _registry.DiscoverAsync(["http://ep:8080/mcp"], factory, CancellationToken.None);

        var ex = Should.Throw<InvalidOperationException>(() => _registry.Resolve("/unknown/file.md"));
        ex.Message.ShouldContain("/library");
    }

    [Fact]
    public async Task DiscoverAsync_DuplicateMountPoint_LastWriteWins()
    {
        var backend1 = CreateMockBackend("lib1");
        var backend2 = CreateMockBackend("lib2");

        var factory = new Mock<IFileSystemBackendFactory>();
        factory.Setup(f => f.DiscoverAsync("http://ep1:8080/mcp", It.IsAny<CancellationToken>()))
            .ReturnsAsync([(new FileSystemMount("lib1", "/library", "First"), backend1)]);
        factory.Setup(f => f.DiscoverAsync("http://ep2:8080/mcp", It.IsAny<CancellationToken>()))
            .ReturnsAsync([(new FileSystemMount("lib2", "/library", "Second"), backend2)]);

        await _registry.DiscoverAsync(
            ["http://ep1:8080/mcp", "http://ep2:8080/mcp"],
            factory.Object, CancellationToken.None);

        var resolution = _registry.Resolve("/library/file.md");
        resolution.Backend.ShouldBe(backend2);
    }

    [Fact]
    public async Task Resolve_CaseInsensitiveMatch()
    {
        var backend = CreateMockBackend("library");
        var factory = CreateMockFactory("http://ep:8080/mcp",
            (new FileSystemMount("library", "/library", "Library"), backend));

        await _registry.DiscoverAsync(["http://ep:8080/mcp"], factory, CancellationToken.None);

        var resolution = _registry.Resolve("/Library/Notes/Todo.md");
        resolution.Backend.ShouldBe(backend);
        resolution.RelativePath.ShouldBe("Notes/Todo.md");
    }

    [Fact]
    public async Task Resolve_PartialSegmentMatch_DoesNotMatch()
    {
        var backend = CreateMockBackend("library");
        var factory = CreateMockFactory("http://ep:8080/mcp",
            (new FileSystemMount("library", "/library", "Library"), backend));

        await _registry.DiscoverAsync(["http://ep:8080/mcp"], factory, CancellationToken.None);

        var ex = Should.Throw<InvalidOperationException>(() => _registry.Resolve("/libraryextra/file.md"));
        ex.Message.ShouldContain("No filesystem mounted");
    }

    private static IFileSystemBackend CreateMockBackend(string name)
    {
        var mock = new Mock<IFileSystemBackend>();
        mock.Setup(b => b.FilesystemName).Returns(name);
        return mock.Object;
    }

    private static IFileSystemBackendFactory CreateMockFactory(
        string endpoint, params (FileSystemMount Mount, IFileSystemBackend Backend)[] results)
    {
        var mock = new Mock<IFileSystemBackendFactory>();
        mock.Setup(f => f.DiscoverAsync(endpoint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(results.ToList());
        return mock.Object;
    }
}
