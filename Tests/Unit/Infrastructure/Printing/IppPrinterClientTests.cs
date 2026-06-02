using System.Text;
using Domain.DTOs.Printing;
using Infrastructure.Clients.Printer;
using Moq;
using SharpIpp;
using SharpIpp.Models.Requests;
using SharpIpp.Models.Responses;
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

    [Fact]
    public async Task GetActiveJobsAsync_DropsFinishedJobs_ButKeepsActiveAndStatelessOnes()
    {
        // A printer that ignores WhichJobs.NotCompleted may return finished jobs too; the client must
        // filter them out so reconciliation can prune them, while keeping jobs that omit job-state.
        var response = new GetJobsResponse
        {
            JobsAttributes =
            [
                new JobDescriptionAttributes { JobId = 1, JobName = "active.txt", JobState = JobState.Processing },
                new JobDescriptionAttributes { JobId = 2, JobName = "pending.txt", JobState = JobState.Pending },
                new JobDescriptionAttributes { JobId = 3, JobName = "done.txt", JobState = JobState.Completed },
                new JobDescriptionAttributes { JobId = 4, JobName = "canceled.txt", JobState = JobState.Canceled },
                new JobDescriptionAttributes { JobId = 5, JobName = "stateless.txt", JobState = null }
            ]
        };

        var client = new Mock<ISharpIppClient>();
        client.Setup(c => c.GetJobsAsync(It.IsAny<GetJobsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var printer = new IppPrinterClient(client.Object, new Uri("ipp://localhost:631/ipp/print"),
            "application/octet-stream", "fit");

        var active = await printer.GetActiveJobsAsync(CancellationToken.None);

        active.Select(j => j.JobId).ShouldBe([1, 2, 5]);
        active.Single(j => j.JobId == 5).State.ShouldBe(PrintJobState.Processing);
    }
}