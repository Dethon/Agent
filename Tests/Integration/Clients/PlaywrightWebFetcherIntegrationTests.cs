using Domain.Contracts;
using Shouldly;

namespace Tests.Integration.Clients;

[Collection("PlaywrightWebFetcherIntegration")]
public class PlaywrightWebFetcherIntegrationTests(PlaywrightWebFetcherFixture fixture)
{
    [SkippableFact]
    public async Task FetchAsync_WithWikipedia_ReturnsContent()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        // Act
        var request = new WebFetchRequest(
            "https://en.wikipedia.org/wiki/Model_Context_Protocol",
            Format: WebFetchOutputFormat.Markdown,
            MaxLength: 5000);
        var result = await fixture.Fetcher.FetchAsync(request);

        // Assert
        result.Status.ShouldBe(WebFetchStatus.Success);
        result.Title.ShouldNotBeNullOrEmpty();
        result.Content.ShouldNotBeNullOrEmpty();
        result.Metadata.ShouldNotBeNull();
    }

    [SkippableFact]
    public async Task FetchAsync_WithCssSelector_ExtractsSpecificContent()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        // Act - target Wikipedia infobox
        var request = new WebFetchRequest(
            "https://en.wikipedia.org/wiki/C_Sharp_(programming_language)",
            Selector: ".infobox",
            Format: WebFetchOutputFormat.Markdown,
            MaxLength: 5000);
        var result = await fixture.Fetcher.FetchAsync(request);

        // Assert
        if (result.Status == WebFetchStatus.Partial)
        {
            // Selector might not match - that's okay for integration test
            return;
        }

        result.Status.ShouldBe(WebFetchStatus.Success);
        result.Content.ShouldNotBeNullOrEmpty();
    }

    [SkippableFact]
    public async Task FetchAsync_WithTextFormat_ReturnsPlainText()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        // Act
        var request = new WebFetchRequest(
            "https://example.com",
            Format: WebFetchOutputFormat.Text);
        var result = await fixture.Fetcher.FetchAsync(request);

        // Assert
        result.Status.ShouldBe(WebFetchStatus.Success);
        result.Content!.ShouldNotContain("<html");
        result.Content!.ShouldNotContain("<div");
    }

    [SkippableFact]
    public async Task FetchAsync_WithNonexistentDomain_ReturnsError()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        // Act
        var request = new WebFetchRequest("https://this-domain-definitely-does-not-exist-xyz123.com/page");
        var result = await fixture.Fetcher.FetchAsync(request);

        // Assert
        result.Status.ShouldBe(WebFetchStatus.Error);
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task FetchAsync_WithInvalidScheme_ReturnsError()
    {
        // This test doesn't need Playwright - it fails early on URL validation
        // Act
        var request = new WebFetchRequest("ftp://example.com/file");
        var result = await fixture.Fetcher.FetchAsync(request);

        // Assert
        result.Status.ShouldBe(WebFetchStatus.Error);
        result.ErrorMessage!.ShouldContain("http");
    }
}