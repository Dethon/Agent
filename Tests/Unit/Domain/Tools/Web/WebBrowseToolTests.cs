using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Web;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Web;

public class WebBrowseToolTests
{
    private readonly Mock<IWebBrowser> _browser = new();
    private readonly TestableWebBrowseTool _tool;

    public WebBrowseToolTests()
    {
        _tool = new TestableWebBrowseTool(_browser.Object);
    }

    [Fact]
    public async Task RunAsync_Success_BodySeparateFromEnvelope()
    {
        var body = "# Heading\n\nLine with \"quotes\" and a \\ backslash.";
        _browser.Setup(b => b.NavigateAsync(It.IsAny<BrowseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowseResult(
                SessionId: "s1",
                Url: "https://example.com",
                Status: BrowseStatus.Success,
                Title: "Example",
                Content: body,
                ContentLength: body.Length,
                Truncated: false,
                Metadata: null,
                StructuredData: null,
                DismissedModals: null,
                ErrorMessage: null));

        var result = await _tool.TestRun("s1", "https://example.com", CancellationToken.None);

        result.Body.ShouldBe(body);
        var envelope = result.Envelope.AsObject();
        envelope.ContainsKey("content").ShouldBeFalse();
        envelope["status"]!.GetValue<string>().ShouldBe("success");
        envelope["url"]!.GetValue<string>().ShouldBe("https://example.com");
        envelope["title"]!.GetValue<string>().ShouldBe("Example");
        envelope["contentLength"]!.GetValue<int>().ShouldBe(body.Length);
    }

    [Fact]
    public async Task RunAsync_Error_BodyIsNull()
    {
        _browser.Setup(b => b.NavigateAsync(It.IsAny<BrowseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowseResult(
                SessionId: "s1",
                Url: "https://example.com",
                Status: BrowseStatus.Error,
                Title: null,
                Content: null,
                ContentLength: 0,
                Truncated: false,
                Metadata: null,
                StructuredData: null,
                DismissedModals: null,
                ErrorMessage: "boom"));

        var result = await _tool.TestRun("s1", "https://example.com", CancellationToken.None);

        result.Body.ShouldBeNull();
        var envelope = result.Envelope.AsObject();
        envelope["ok"]!.GetValue<bool>().ShouldBeFalse();
    }

    private class TestableWebBrowseTool(IWebBrowser browser) : WebBrowseTool(browser)
    {
        public Task<WebBrowseToolResult> TestRun(string sessionId, string url, CancellationToken ct)
            => RunAsync(sessionId, url, null, 10000, 0, false, false, 3, ct);
    }
}
