using Domain.DTOs;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

// A pathological caller-supplied search regex must surface as a graceful timeout envelope, not an
// uncaught RegexMatchTimeoutException leaking to the MCP boundary. The match timeout is injected
// tiny so the catastrophic backtracking trips it deterministically and fast. (Glob needs no such
// test: GlobToRegex only emits literals + collapsible '.*', which .NET reduces so it can't blow up.)
public class HaFileSystemTimeoutTests
{
    [Fact]
    public async Task SearchAsync_PathologicalRegex_ReturnsTimeoutEnvelope()
    {
        var client = new FakeHaClient { States = { Entity("light.kitchen", new string('a', 60)) } };
        var fs = new HaFileSystem(
            new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client,
            regexMatchTimeout: TimeSpan.FromMilliseconds(1));

        var result = await fs.SearchAsync(
            "(a+)+b", regex: true, null, null, null, 50, 1, VfsTextSearchOutputMode.Content, CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("timeout");
        result["hint"]!.GetValue<string>().ShouldContain("regex=false");
    }
}