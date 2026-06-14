using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

[Collection("WebChatE2E")]
[Trait("Category", "E2E")]
public sealed class HearthNavigationE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task DesktopViewport_ShowsTheRail()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(1280, 900);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        var rail = page.Locator(".hearth .agent-segmented");
        await rail.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000, State = WaitForSelectorState.Visible });
        (await rail.IsVisibleAsync()).ShouldBeTrue();
    }

    [SkippableFact]
    public async Task MobileViewport_ShowsPeekBarAndExpandsOnHandleTap()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // Peek bar present; rail segmented strip hidden on mobile.
        await page.Locator(".hearth-peek").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // Tapping the handle cycles to half/full and reveals the search field.
        await page.Locator(".hearth-handle").ClickAsync();
        await page.Locator(".hearth-handle").ClickAsync();
        await Assertions.Expect(page.Locator(".hearth-search-input")).ToBeVisibleAsync();
    }
}