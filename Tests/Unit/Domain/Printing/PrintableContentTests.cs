using System.Text;
using Domain.Tools.Printing;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Printing;

public class PrintableContentTests
{
    public static TheoryData<byte[], string> FormatCases() => new()
    {
        { new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "jpeg" },
        { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "png" },
        { Encoding.ASCII.GetBytes("%PDF-1.7\n"), "pdf" },
        { Encoding.ASCII.GetBytes("GIF89a"), "gif" },
        { Encoding.ASCII.GetBytes("RaS2\x00\x00"), "pwg-raster" },
        { new byte[] { 0x1B, 0x45 }, "pcl" },
        { Encoding.UTF8.GetBytes("hello\nworld"), "text" },
        { new byte[] { 0x00, 0x01, 0x02, 0xAB }, "unknown" },
        { Array.Empty<byte>(), "text" }
    };

    [Theory]
    [MemberData(nameof(FormatCases))]
    public void DetectFormat_ClassifiesByMagicBytes(byte[] bytes, string expected)
    {
        PrintableContent.DetectFormat(bytes).ShouldBe(expected);
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