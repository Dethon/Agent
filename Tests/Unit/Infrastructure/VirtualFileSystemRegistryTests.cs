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
    public void Mount_RegistersMount()
    {
        var backend = CreateMockBackend("library");
        _registry.Mount(new FileSystemMount("library", "/library", "Personal document library"), backend);

        var mounts = _registry.GetMounts();
        mounts.Count.ShouldBe(1);
        mounts[0].Name.ShouldBe("library");
        mounts[0].MountPoint.ShouldBe("/library");
    }

    [Fact]
    public void Mount_MultipleMounts_RegistersAll()
    {
        var libraryBackend = CreateMockBackend("library");
        var vaultBackend = CreateMockBackend("vault");

        _registry.Mount(new FileSystemMount("library", "/library", "Library"), libraryBackend);
        _registry.Mount(new FileSystemMount("vault", "/vault", "Vault"), vaultBackend);

        _registry.GetMounts().Count.ShouldBe(2);
    }

    [Fact]
    public void Resolve_MatchingMount_ReturnsBackendAndRelativePath()
    {
        var backend = CreateMockBackend("library");
        _registry.Mount(new FileSystemMount("library", "/library", "Library"), backend);

        var resolution = _registry.Resolve("/library/notes/todo.md");
        resolution.Backend.ShouldBe(backend);
        resolution.RelativePath.ShouldBe("notes/todo.md");
    }

    [Fact]
    public void Resolve_RootPath_ReturnsEmptyRelativePath()
    {
        var backend = CreateMockBackend("library");
        _registry.Mount(new FileSystemMount("library", "/library", "Library"), backend);

        var resolution = _registry.Resolve("/library");
        resolution.Backend.ShouldBe(backend);
        resolution.RelativePath.ShouldBe("");
    }

    [Fact]
    public void Resolve_LongestPrefixWins()
    {
        var libraryBackend = CreateMockBackend("library");
        var docsBackend = CreateMockBackend("docs");

        _registry.Mount(new FileSystemMount("library", "/library", "Library"), libraryBackend);
        _registry.Mount(new FileSystemMount("docs", "/library/docs", "Docs"), docsBackend);

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
    public void Resolve_NoMatchingMount_ErrorListsAvailable()
    {
        var backend = CreateMockBackend("library");
        _registry.Mount(new FileSystemMount("library", "/library", "Library"), backend);

        var ex = Should.Throw<InvalidOperationException>(() => _registry.Resolve("/unknown/file.md"));
        ex.Message.ShouldContain("/library");
    }

    [Fact]
    public void Mount_DuplicateMountPoint_LastWriteWins()
    {
        var backend1 = CreateMockBackend("lib1");
        var backend2 = CreateMockBackend("lib2");

        _registry.Mount(new FileSystemMount("lib1", "/library", "First"), backend1);
        _registry.Mount(new FileSystemMount("lib2", "/library", "Second"), backend2);

        var resolution = _registry.Resolve("/library/file.md");
        resolution.Backend.ShouldBe(backend2);
    }

    [Fact]
    public void Resolve_CaseInsensitiveMatch()
    {
        var backend = CreateMockBackend("library");
        _registry.Mount(new FileSystemMount("library", "/library", "Library"), backend);

        var resolution = _registry.Resolve("/Library/Notes/Todo.md");
        resolution.Backend.ShouldBe(backend);
        resolution.RelativePath.ShouldBe("Notes/Todo.md");
    }

    [Fact]
    public void Resolve_PartialSegmentMatch_DoesNotMatch()
    {
        var backend = CreateMockBackend("library");
        _registry.Mount(new FileSystemMount("library", "/library", "Library"), backend);

        var ex = Should.Throw<InvalidOperationException>(() => _registry.Resolve("/libraryextra/file.md"));
        ex.Message.ShouldContain("No filesystem mounted");
    }

    private static IFileSystemBackend CreateMockBackend(string name)
    {
        var mock = new Mock<IFileSystemBackend>();
        mock.Setup(b => b.FilesystemName).Returns(name);
        return mock.Object;
    }
}
