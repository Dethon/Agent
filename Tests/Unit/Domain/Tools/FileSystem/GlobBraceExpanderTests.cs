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

    [Theory]
    [InlineData("file{1}.txt")]  // no comma → not a group
    [InlineData("a{b,c")]        // unmatched opening brace
    public void Expand_NoBraceExpansion_ReturnsSingleLiteralPattern(string pattern)
    {
        GlobBraceExpander.Expand(pattern).ShouldBe([pattern]);
    }
}