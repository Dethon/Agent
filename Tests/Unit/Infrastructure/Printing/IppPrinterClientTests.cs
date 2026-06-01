using System.Text;
using Infrastructure.Clients.Printer;
using Shouldly;
using Xunit;

namespace Tests.Unit.Infrastructure.Printing;

public class IppPrinterClientTests
{
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
}