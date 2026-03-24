using Shouldly;

namespace Tests.E2E.Fixtures;

public class E2EFixtureBaseTests
{
    [Fact]
    [Trait("Category", "E2E")]
    public async Task CreatePage_ReturnsUsablePage()
    {
        // Use a concrete subclass that doesn't need containers
        await using var fixture = new StandalonePlaywrightFixture();
        await fixture.InitializeAsync();

        var page = await fixture.CreatePageAsync();

        page.ShouldNotBeNull();
        await page.GotoAsync("about:blank");
        var title = await page.TitleAsync();
        title.ShouldNotBeNull();
    }
}

// Minimal concrete subclass for testing the base
file class StandalonePlaywrightFixture : E2EFixtureBase
{
    protected override Task StartContainersAsync(CancellationToken ct) => Task.CompletedTask;
    protected override Task StopContainersAsync() => Task.CompletedTask;
}
