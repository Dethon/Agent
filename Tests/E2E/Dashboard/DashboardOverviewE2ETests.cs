using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Dashboard;

[Collection("DashboardE2E")]
[Trait("Category", "E2E")]
public class DashboardOverviewE2ETests(DashboardE2EFixture fixture)
{
    [Fact]
    public async Task LoadOverview_ShowsKpiCards()
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.DashboardUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        var kpiCards = page.Locator(".kpi-card");
        var count = await kpiCards.CountAsync();
        count.ShouldBe(5);

        var labels = await kpiCards.Locator(".kpi-label").AllTextContentsAsync();
        labels.ShouldContain("Input Tokens");
        labels.ShouldContain("Output Tokens");
        labels.ShouldContain("Cost");
        labels.ShouldContain("Tool Calls");
        labels.ShouldContain("Errors");
    }

    [Fact]
    public async Task LoadOverview_ShowsHealthGrid()
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.DashboardUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        var healthGrid = page.Locator(".health-grid");
        (await healthGrid.CountAsync()).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task LoadOverview_ShowsConnectionStatus()
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.DashboardUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        var status = page.Locator(".connection-status");
        await status.WaitForAsync(new() { Timeout = 10_000 });
        (await status.IsVisibleAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task TimeFilter_ChangesData()
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.DashboardUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        // Click the "7d" pill button inside .pill-selector
        var pill7d = page.Locator(".pill-selector .pill", new() { HasText = "7d" });
        await pill7d.ClickAsync();

        // Verify the pill now has the "active" class
        await Assertions.Expect(pill7d).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("active"));
    }
}
