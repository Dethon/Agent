using Microsoft.Playwright;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Dashboard;

[Collection("DashboardE2E")]
[Trait("Category", "E2E")]
public class DashboardNavigationE2ETests(DashboardE2EFixture fixture)
{
    [Theory]
    [InlineData("/tokens", "Token Usage")]
    [InlineData("/tools", "Tool Calls")]
    [InlineData("/errors", "Errors")]
    [InlineData("/schedules", "Schedule Executions")]
    [InlineData("/memory", "Memory")]
    [InlineData("/latency", "Latency")]
    [InlineData("/voice", "Voice")]
    public async Task NavigateToPage_ShowsCorrectPage(string href, string expectedTitle)
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.DashboardUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        var navLink = page.Locator($"nav.sidebar a[href='{href}']");
        await navLink.ClickAsync();

        await page.WaitForURLAsync($"**{href}");

        var header = page.Locator("h2");
        await Assertions.Expect(header).ToContainTextAsync(expectedTitle);
    }
}