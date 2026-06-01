using System.Text;
using Infrastructure.Clients.Printer;
using SharpIpp.Protocol.Models;
using Shouldly;
using Xunit;

namespace Tests.Unit.Infrastructure.Printing;

public class IppPrinterClientTests
{
    [Theory]
    [InlineData("fit", "fit")]
    [InlineData("auto-fit", "auto-fit")]
    [InlineData("auto", "auto")]
    [InlineData("fill", "fill")]
    [InlineData("none", "none")]
    [InlineData("FIT", "fit")]
    [InlineData("bogus", "fit")]
    [InlineData("", "fit")]
    public void ParseScaling_MapsKnownValues_AndFallsBackToFit(string input, string expected)
    {
        IppPrinterClient.ParseScaling(input).Value.ShouldBe(expected);
    }

    [Fact]
    public void PreparePayload_TextWithLf_ConvertsToCrlf()
    {
        var bytes = Encoding.UTF8.GetBytes("line1\nline2\nline3");
        var result = IppPrinterClient.PreparePayload("text/plain", bytes);
        Encoding.UTF8.GetString(result).ShouldBe("line1\r\nline2\r\nline3");
    }

    [Fact]
    public void PreparePayload_TextWithExistingCrlf_IsNotDoubled()
    {
        var bytes = Encoding.UTF8.GetBytes("a\r\nb");
        var result = IppPrinterClient.PreparePayload("text/plain", bytes);
        Encoding.UTF8.GetString(result).ShouldBe("a\r\nb");
    }

    [Fact]
    public void PreparePayload_TextWithLoneCr_ConvertsToCrlf()
    {
        var bytes = Encoding.UTF8.GetBytes("a\rb");
        var result = IppPrinterClient.PreparePayload("text/plain", bytes);
        Encoding.UTF8.GetString(result).ShouldBe("a\r\nb");
    }

    [Fact]
    public void PreparePayload_TextMarkdown_IsNormalized()
    {
        var bytes = Encoding.UTF8.GetBytes("x\ny");
        var result = IppPrinterClient.PreparePayload("text/markdown", bytes);
        Encoding.UTF8.GetString(result).ShouldBe("x\r\ny");
    }

    [Fact]
    public void PreparePayload_BinaryContent_IsReturnedUnchanged()
    {
        // Contains 0x0A (LF) bytes that must NOT be rewritten inside binary data.
        var bytes = new byte[] { 0xFF, 0xD8, 0x0A, 0x00, 0x0A, 0xFF };
        var result = IppPrinterClient.PreparePayload("application/octet-stream", bytes);
        result.ShouldBe(bytes);
    }

    [Fact]
    public void PreparePayload_Jpeg_IsReturnedUnchanged()
    {
        var bytes = new byte[] { 0xFF, 0xD8, 0x0A, 0x10 };
        IppPrinterClient.PreparePayload("image/jpeg", bytes).ShouldBe(bytes);
    }

    [Fact]
    public void PreparePayload_OctetStreamTextWithLf_ConvertsToCrlf()
    {
        // Cross-backend copies arrive as octet-stream but are really text — sniff and normalize.
        var bytes = Encoding.UTF8.GetBytes("copied\nlines\nhere");
        var result = IppPrinterClient.PreparePayload("application/octet-stream", bytes);
        Encoding.UTF8.GetString(result).ShouldBe("copied\r\nlines\r\nhere");
    }

    [Fact]
    public void PreparePayload_OctetStreamBinaryWithNul_IsUnchanged()
    {
        // A NUL byte marks this octet-stream payload as binary — must not be rewritten.
        var bytes = new byte[] { 0x89, 0x50, 0x00, 0x0A, 0x1A, 0x0A };
        IppPrinterClient.PreparePayload("application/octet-stream", bytes).ShouldBe(bytes);
    }

    [Fact]
    public void PreparePayload_ExplicitBinaryType_WithTextLikeBytes_IsUnchanged()
    {
        // An explicit binary type is honored even when the bytes look like text.
        var bytes = Encoding.UTF8.GetBytes("a\nb");
        IppPrinterClient.PreparePayload("image/jpeg", bytes).ShouldBe(bytes);
    }
}