using Domain.Tools.FileSystem;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class GlobBraceExpanderTests
{
    [Fact]
    public void Expand_NoBraces_ReturnsPatternUnchanged()
    {
        GlobBraceExpander.Expand("**/*.txt").ShouldBe(["**/*.txt"]);
    }

    [Fact]
    public void Expand_SingleGroup_ExpandsEachAlternativeInOrder()
    {
        GlobBraceExpander.Expand("**/*.{jpg,jpeg,png,gif,bmp,tiff,webp}").ShouldBe([
            "**/*.jpg", "**/*.jpeg", "**/*.png", "**/*.gif", "**/*.bmp", "**/*.tiff", "**/*.webp"
        ]);
    }

    [Fact]
    public void Expand_MultipleGroups_ProducesCartesianProduct()
    {
        GlobBraceExpander.Expand("{a,b}/{c,d}").ShouldBe(["a/c", "a/d", "b/c", "b/d"]);
    }

    [Fact]
    public void Expand_NestedGroups_FlattensToAllLeaves()
    {
        GlobBraceExpander.Expand("img.{a,{b,c}}").ShouldBe(["img.a", "img.b", "img.c"]);
    }

    [Fact]
    public void Expand_EmptyAlternative_YieldsEmptyString()
    {
        GlobBraceExpander.Expand("file{,.bak}").ShouldBe(["file", "file.bak"]);
    }

    [Fact]
    public void Expand_GroupWithoutComma_IsTreatedAsLiteral()
    {
        GlobBraceExpander.Expand("file{1}.txt").ShouldBe(["file{1}.txt"]);
    }

    [Fact]
    public void Expand_UnmatchedBrace_IsTreatedAsLiteral()
    {
        GlobBraceExpander.Expand("a{b,c").ShouldBe(["a{b,c"]);
    }
}