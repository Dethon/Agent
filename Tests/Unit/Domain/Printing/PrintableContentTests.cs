using System.Text;
using Domain.Tools.Printing;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Printing;

public class PrintableContentTests
{
    [Fact]
    public void DetectFormat_Jpeg()
    {
        PrintableContent.DetectFormat(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }).ShouldBe("jpeg");
    }

    [Fact]
    public void DetectFormat_Png()
    {
        PrintableContent.DetectFormat(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }).ShouldBe("png");
    }

    [Fact]
    public void DetectFormat_Pdf()
    {
        PrintableContent.DetectFormat(Encoding.ASCII.GetBytes("%PDF-1.7\n")).ShouldBe("pdf");
    }

    [Fact]
    public void DetectFormat_Gif()
    {
        PrintableContent.DetectFormat(Encoding.ASCII.GetBytes("GIF89a")).ShouldBe("gif");
    }

    [Fact]
    public void DetectFormat_PwgRaster()
    {
        PrintableContent.DetectFormat(Encoding.ASCII.GetBytes("RaS2\x00\x00")).ShouldBe("pwg-raster");
    }

    [Fact]
    public void DetectFormat_Pcl()
    {
        PrintableContent.DetectFormat(new byte[] { 0x1B, 0x45 }).ShouldBe("pcl");
    }

    [Fact]
    public void DetectFormat_Text()
    {
        PrintableContent.DetectFormat(Encoding.UTF8.GetBytes("hello\nworld")).ShouldBe("text");
    }

    [Fact]
    public void DetectFormat_UnknownBinary()
    {
        PrintableContent.DetectFormat(new byte[] { 0x00, 0x01, 0x02, 0xAB }).ShouldBe("unknown");
    }

    [Fact]
    public void DetectFormat_Empty_IsText()
    {
        PrintableContent.DetectFormat(ReadOnlySpan<byte>.Empty).ShouldBe("text");
    }

    [Theory]
    [InlineData("jpeg", "text,jpeg,pcl", true)]
    [InlineData("png", "text,jpeg,pcl", false)]
    [InlineData("TEXT", "text, jpeg", true)]
    [InlineData("pdf", "text,jpeg", false)]
    public void IsSupported_ChecksAllowListCaseInsensitively(string format, string csv, bool expected)
    {
        PrintableContent.IsSupported(format, csv).ShouldBe(expected);
    }
}