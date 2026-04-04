using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class FileSystemToolFeatureTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly FileSystemToolFeature _feature;

    public FileSystemToolFeatureTests()
    {
        _registry.Setup(r => r.GetMounts()).Returns([
            new FileSystemMount("library", "/library", "Personal document library")
        ]);
        _feature = new FileSystemToolFeature(_registry.Object);
    }

    [Fact]
    public void FeatureName_IsFilesystem()
    {
        _feature.FeatureName.ShouldBe("filesystem");
    }

    [Fact]
    public void GetTools_NullEnabledTools_ReturnsAllTools()
    {
        var config = new FeatureConfig();
        var tools = _feature.GetTools(config).ToList();

        tools.Count.ShouldBe(7);
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:text_read");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:text_create");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:text_edit");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:glob_files");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:text_search");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:move");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:remove");
    }

    [Fact]
    public void GetTools_FilteredEnabledTools_ReturnsOnlyMatching()
    {
        var config = new FeatureConfig(
            EnabledTools: new HashSet<string>(["read", "move"], StringComparer.OrdinalIgnoreCase));
        var tools = _feature.GetTools(config).ToList();

        tools.Count.ShouldBe(2);
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:text_read");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:move");
    }

    [Fact]
    public void GetTools_EmptyEnabledTools_ReturnsNoTools()
    {
        var config = new FeatureConfig(
            EnabledTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var tools = _feature.GetTools(config).ToList();

        tools.ShouldBeEmpty();
    }

    [Fact]
    public void Prompt_ContainsMountPoints()
    {
        _feature.Prompt.ShouldNotBeNull();
        _feature.Prompt.ShouldContain("/library");
        _feature.Prompt.ShouldContain("Personal document library");
    }

    [Fact]
    public void Prompt_ReturnsNull_WhenNoMounts()
    {
        var emptyRegistry = new Mock<IVirtualFileSystemRegistry>();
        emptyRegistry.Setup(r => r.GetMounts()).Returns([]);
        var feature = new FileSystemToolFeature(emptyRegistry.Object);

        feature.Prompt.ShouldBeNull();
    }
}
