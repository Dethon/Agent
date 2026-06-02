using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Printing;

public class PrintingPromptTests
{
    [Fact]
    public void DescribeFormats_MapsKnownTokens_ToFriendlyNames()
    {
        PrintingPrompt.DescribeFormats("text,jpeg,pwg-raster,urf,pcl")
            .ShouldBe("plain text, JPEG images, PWG Raster, Apple URF, PCL");
    }

    [Fact]
    public void DescribeFormats_TrimsAndIsCaseInsensitive()
    {
        PrintingPrompt.DescribeFormats(" TEXT , JPEG ").ShouldBe("plain text, JPEG images");
    }

    [Fact]
    public void DescribeFormats_UnknownToken_FallsBackToRawToken()
    {
        PrintingPrompt.DescribeFormats("text,webp").ShouldBe("plain text, webp");
    }

    [Fact]
    public void DescribeFormats_Empty_SaysNothing()
    {
        PrintingPrompt.DescribeFormats("").ShouldBe("nothing");
    }

    [Fact]
    public void Build_AdvertisesExactlyTheConfiguredFormats()
    {
        PrintingPrompt.Build("text,jpeg").ShouldContain("This printer accepts: plain text, JPEG images.");
    }
}