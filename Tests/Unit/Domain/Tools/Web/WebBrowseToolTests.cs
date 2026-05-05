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

    [Fact]
    public async Task RunAsync_SnapshotTrue_IncludesSnapshotBodyAndRefCount()
    {
        var body = "# Heading";
        var snapshot = "main \"role=main\"\n  button \"Submit\" e-1";
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
        _browser.Setup(b => b.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SnapshotResult("s1", "https://example.com", snapshot, 1, null));

        var result = await _tool.TestRunWithSnapshot("s1", "https://example.com", snapshot: true, CancellationToken.None);

        result.Body.ShouldBe(body);
        result.Snapshot.ShouldBe(snapshot);
        result.Envelope.AsObject()["refCount"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_SnapshotFalse_DoesNotInvokeSnapshot()
    {
        var body = "# Heading";
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

        var result = await _tool.TestRunWithSnapshot("s1", "https://example.com", snapshot: false, CancellationToken.None);

        result.Snapshot.ShouldBeNull();
        result.Envelope.AsObject().ContainsKey("refCount").ShouldBeFalse();
        _browser.Verify(b => b.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private class TestableWebBrowseTool(IWebBrowser browser) : WebBrowseTool(browser)
    {
        public Task<WebBrowseToolResult> TestRun(string sessionId, string url, CancellationToken ct)
            => RunAsync(sessionId, url, null, 10000, 0, false, false, 3, false, ct);

        public Task<WebBrowseToolResult> TestRunWithSnapshot(string sessionId, string url, bool snapshot, CancellationToken ct)
            => RunAsync(sessionId, url, null, 10000, 0, false, false, 3, snapshot, ct);
    }
}
