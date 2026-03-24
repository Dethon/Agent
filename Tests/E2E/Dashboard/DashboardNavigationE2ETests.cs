using Microsoft.Playwright;
using Shouldly;
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
    public async Task NavigateToPage_ShowsCorrectPage(string href, string expectedTitle)
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.DashboardUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        // Click the sidebar nav link
        var navLink = page.Locator($"nav.sidebar a[href='{href}']");
        await navLink.ClickAsync();

        // Wait for navigation
        await page.WaitForURLAsync($"**{href}");

        // Verify the page header contains the expected title
        var header = page.Locator("h2");
        await Assertions.Expect(header).ToContainTextAsync(expectedTitle);
    }
}
