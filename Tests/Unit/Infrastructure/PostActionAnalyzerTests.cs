using Infrastructure.Clients.Browser;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class PostActionAnalyzerTests
{
    [Fact]
    public void DetermineResponseTier_WithWidget_ReturnsWidgetTier()
    {
        var tier = PostActionAnalyzer.DetermineResponseTier(
            widgetDetected: true,
            urlChanged: false,
            contentChangeFraction: 0.05);

        tier.ShouldBe(ResponseTier.Widget);
    }

    [Fact]
    public void DetermineResponseTier_WithUrlChange_ReturnsFullPageTier()
    {
        var tier = PostActionAnalyzer.DetermineResponseTier(
            widgetDetected: false,
            urlChanged: true,
            contentChangeFraction: 0.8);

        tier.ShouldBe(ResponseTier.FullPage);
    }

    [Fact]
    public void DetermineResponseTier_WithMajorContentChange_ReturnsFullPageTier()
    {
        var tier = PostActionAnalyzer.DetermineResponseTier(
            widgetDetected: false,
            urlChanged: false,
            contentChangeFraction: 0.55);

        tier.ShouldBe(ResponseTier.FullPage);
    }

    [Fact]
    public void DetermineResponseTier_WithMinorChange_ReturnsFocusedTier()
    {
        var tier = PostActionAnalyzer.DetermineResponseTier(
            widgetDetected: false,
            urlChanged: false,
            contentChangeFraction: 0.1);

        tier.ShouldBe(ResponseTier.Focused);
    }

    [Fact]
    public void DetermineResponseTier_WidgetTakesPriorityOverUrlChange()
    {
        var tier = PostActionAnalyzer.DetermineResponseTier(
            widgetDetected: true,
            urlChanged: true,
            contentChangeFraction: 0.9);

        tier.ShouldBe(ResponseTier.Widget);
    }

    [Fact]
    public void ComputeContentChangeFraction_IdenticalContent_ReturnsZero()
    {
        var fraction = PostActionAnalyzer.ComputeContentChangeFraction(
            "Hello world", "Hello world");

        fraction.ShouldBe(0.0);
    }

    [Fact]
    public void ComputeContentChangeFraction_CompletelyDifferent_ReturnsOne()
    {
        var fraction = PostActionAnalyzer.ComputeContentChangeFraction(
            "aaaaaaaaaa", "bbbbbbbbbb");

        fraction.ShouldBe(1.0);
    }

    [Fact]
    public void ComputeContentChangeFraction_EmptyBefore_ReturnsOne()
    {
        var fraction = PostActionAnalyzer.ComputeContentChangeFraction(
            "", "some content");

        fraction.ShouldBe(1.0);
    }

    [Fact]
    public void ComputeContentChangeFraction_SmallChange_ReturnsSmallValue()
    {
        var before = "The quick brown fox jumps over the lazy dog. Some more text here to make it longer.";
        var after = "The quick brown fox jumps over the lazy cat. Some more text here to make it longer.";

        var fraction = PostActionAnalyzer.ComputeContentChangeFraction(before, after);

        fraction.ShouldBeGreaterThan(0.0);
        fraction.ShouldBeLessThan(0.2);
    }
}
