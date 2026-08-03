using Domain.Tools.Files;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Files;

public class PathJailTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"jail-{Guid.NewGuid():N}");
    private readonly PathJail _jail;

    public PathJailTests()
    {
        Directory.CreateDirectory(_root);
        _jail = new PathJail(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void Root_IsCanonicalAndHasNoTrailingSeparator()
    {
        var jail = new PathJail(_root + Path.DirectorySeparatorChar);

        jail.Root.ShouldBe(Path.GetFullPath(_root));
    }

    // The sandbox mounts the container root itself, where the root already ends in a separator.
    [Fact]
    public void Contains_WhenTheRootIsTheFilesystemRoot_PathsUnderItAreInside()
    {
        var jail = new PathJail(Path.GetPathRoot(Path.GetTempPath())!);

        jail.Contains(Path.GetTempPath()).ShouldBeTrue();
        jail.TryResolve(Path.Combine(Path.GetTempPath(), "note.md"), out _).ShouldBeTrue();
    }

    [Fact]
    public void Contains_TheRootItself_IsInside()
    {
        _jail.Contains(_jail.Root).ShouldBeTrue();
    }

    [Fact]
    public void Contains_APathUnderTheRoot_IsInside()
    {
        _jail.Contains(Path.Combine(_jail.Root, "docs", "note.md")).ShouldBeTrue();
    }

    // The hole this type closes: a prefix match without a separator let /library-backup pass as
    // though it were inside /library.
    [Fact]
    public void Contains_ASiblingWhoseNameExtendsTheRoot_IsOutside()
    {
        _jail.Contains(_jail.Root + "-backup").ShouldBeFalse();
        _jail.Contains(_jail.Root + "-backup" + Path.DirectorySeparatorChar + "note.md").ShouldBeFalse();
    }

    [Fact]
    public void TryResolve_RelativePath_IsCombinedWithTheRoot()
    {
        Resolved("docs/note.md").ShouldBe(Path.Combine(_jail.Root, "docs", "note.md"));
    }

    [Fact]
    public void TryResolve_AbsolutePathInsideTheRoot_IsAccepted()
    {
        var inside = Path.Combine(_jail.Root, "note.md");

        Resolved(inside).ShouldBe(inside);
    }

    // A path reached through a relative segment is judged by where it lands, not how it is spelt.
    [Fact]
    public void TryResolve_RelativeSegmentThatStaysInside_IsAccepted()
    {
        Resolved("docs/../note.md").ShouldBe(Path.Combine(_jail.Root, "note.md"));
    }

    [Fact]
    public void TryResolve_RelativeSegmentThatEscapes_IsRefused()
    {
        _jail.TryResolve("../elsewhere/note.md", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryResolve_AbsolutePathOutsideTheRoot_ReturnsFalseWithoutThrowing()
    {
        _jail.TryResolve("/etc/passwd", out var full).ShouldBeFalse();
        full.ShouldBeNull();
    }

    private string Resolved(string path)
    {
        _jail.TryResolve(path, out var full).ShouldBeTrue();
        return full!;
    }
}