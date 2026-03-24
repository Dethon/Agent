using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.Dashboard;

[Collection("DashboardE2E")]
[Trait("Category", "E2E")]
public class DashboardFixtureTests(DashboardE2EFixture fixture)
{
    [Fact]
    public async Task Fixture_ProvidesAccessibleDashboardUrl()
    {
        fixture.DashboardUrl.ShouldNotBeNullOrEmpty();

        var page = await fixture.CreatePageAsync();
        var response = await page.GotoAsync(fixture.DashboardUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        response.ShouldNotBeNull();
        response.Ok.ShouldBeTrue();
    }
}
