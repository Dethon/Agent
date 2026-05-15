using Domain.Contracts;
using Domain.Tools.Web;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Web;

public class WebSnapshotToolTests
{
    private readonly Mock<IWebBrowser> _browser = new();
    private readonly TestableWebSnapshotTool _tool;

    public WebSnapshotToolTests()
    {
        _tool = new TestableWebSnapshotTool(_browser.Object);
    }

    [Fact]
    public async Task RunAsync_Success_BodySeparateFromEnvelope()
    {
        var snapshot = "main \"role=main\"\n  link \"href=/about\" e-1\n  button \"Submit\" e-2";
        _browser.Setup(b => b.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SnapshotResult(
                SessionId: "s1",
                Url: "https://example.com",
                Snapshot: snapshot,
                RefCount: 2,
                ErrorMessage: null));

        var result = await _tool.TestRun("s1", null, CancellationToken.None);

        result.Body.ShouldBe(snapshot);
        var envelope = result.Envelope.AsObject();
        envelope.ContainsKey("snapshot").ShouldBeFalse();
        envelope["status"]!.GetValue<string>().ShouldBe("success");
        envelope["sessionId"]!.GetValue<string>().ShouldBe("s1");
        envelope["url"]!.GetValue<string>().ShouldBe("https://example.com");
        envelope["refCount"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public async Task RunAsync_Error_BodyIsNull()
    {
        _browser.Setup(b => b.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SnapshotResult(
                SessionId: "s1",
                Url: null,
                Snapshot: null,
                RefCount: 0,
                ErrorMessage: "snapshot failed"));

        var result = await _tool.TestRun("s1", null, CancellationToken.None);

        result.Body.ShouldBeNull();
        result.Envelope.AsObject()["ok"]!.GetValue<bool>().ShouldBeFalse();
    }

    private class TestableWebSnapshotTool(IWebBrowser browser) : WebSnapshotTool(browser)
    {
        public Task<WebSnapshotToolResult> TestRun(string sessionId, string? selector, CancellationToken ct)
            => RunAsync(sessionId, selector, ct);
    }
}