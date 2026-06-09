using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Printing;

public class PrintingPromptTests
{
    [Theory]
    [InlineData("text,jpeg,pwg-raster,urf,pcl", "plain text, JPEG images, PWG Raster, Apple URF, PCL")]
    [InlineData(" TEXT , JPEG ", "plain text, JPEG images")]
    [InlineData("text,webp", "plain text, webp")]
    [InlineData("", "nothing")]
    public void DescribeFormats_MapsTokens(string csv, string expected)
    {
        PrintingPrompt.DescribeFormats(csv).ShouldBe(expected);
    }

    [Fact]
    public void Build_AdvertisesExactlyTheConfiguredFormats()
    {
        PrintingPrompt.Build("text,jpeg").ShouldContain("This printer accepts: plain text, JPEG images.");
    }
}