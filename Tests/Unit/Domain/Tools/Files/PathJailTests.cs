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
    public void Resolve_RelativePath_IsCombinedWithTheRoot()
    {
        _jail.Resolve("docs/note.md").ShouldBe(Path.Combine(_jail.Root, "docs", "note.md"));
    }

    [Fact]
    public void Resolve_AbsolutePathInsideTheRoot_IsAccepted()
    {
        var inside = Path.Combine(_jail.Root, "note.md");

        _jail.Resolve(inside).ShouldBe(inside);
    }

    // A path reached through a relative segment is judged by where it lands, not how it is spelt.
    [Fact]
    public void Resolve_RelativeSegmentThatStaysInside_IsAccepted()
    {
        _jail.Resolve("docs/../note.md").ShouldBe(Path.Combine(_jail.Root, "note.md"));
    }

    [Fact]
    public void Resolve_RelativeSegmentThatEscapes_IsRefused()
    {
        Should.Throw<UnauthorizedAccessException>(() => _jail.Resolve("../elsewhere/note.md"));
    }

    [Fact]
    public void Resolve_AbsolutePathOutsideTheRoot_IsRefused()
    {
        Should.Throw<UnauthorizedAccessException>(() => _jail.Resolve("/etc/passwd"));
    }

    [Fact]
    public void TryResolve_OutsideTheRoot_ReturnsFalseWithoutThrowing()
    {
        _jail.TryResolve("/etc/passwd", out var full).ShouldBeFalse();
        full.ShouldBeNull();
    }
}