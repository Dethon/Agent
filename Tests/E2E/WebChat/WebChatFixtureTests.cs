using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

[Collection("WebChatE2E")]
[Trait("Category", "E2E")]
public class WebChatFixtureTests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task Fixture_ProvidesAccessibleWebChatUrl()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat E2E stack not available (missing API key?)");

        var page = await fixture.CreatePageAsync();
        var response = await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        response.ShouldNotBeNull();
        response.Ok.ShouldBeTrue();
    }
}
