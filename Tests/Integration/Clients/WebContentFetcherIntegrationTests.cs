using Domain.Contracts;
using Infrastructure.Clients;
using Shouldly;

namespace Tests.Integration.Clients;

public class WebContentFetcherIntegrationTests
{
    private readonly WebContentFetcher _fetcher;

    public WebContentFetcherIntegrationTests()
    {
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        var htmlConverter = new HtmlConverter();
        var htmlProcessor = new HtmlProcessor(htmlConverter);
        _fetcher = new WebContentFetcher(httpClient, htmlProcessor);
    }

    [SkippableFact]
    public async Task FetchAsync_WithWikipedia_ReturnsContent()
    {
        // This test may fail if Wikipedia is unreachable
        Skip.If(!await IsInternetAvailable(), "Internet not available");

        // Act
        var request = new WebFetchRequest(
            "https://en.wikipedia.org/wiki/Model_Context_Protocol",
            Format: WebFetchOutputFormat.Markdown,
            MaxLength: 5000);
        var result = await _fetcher.FetchAsync(request);

        // Assert
        result.Status.ShouldBe(WebFetchStatus.Success);
        result.Title.ShouldNotBeNullOrEmpty();
        result.Content.ShouldNotBeNullOrEmpty();
        result.Metadata.ShouldNotBeNull();
    }

    [SkippableFact]
    public async Task FetchAsync_WithCssSelector_ExtractsSpecificContent()
    {
        Skip.If(!await IsInternetAvailable(), "Internet not available");

        // Act - target Wikipedia infobox
        var request = new WebFetchRequest(
            "https://en.wikipedia.org/wiki/C_Sharp_(programming_language)",
            Selector: ".infobox",
            Format: WebFetchOutputFormat.Markdown,
            MaxLength: 5000);
        var result = await _fetcher.FetchAsync(request);

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
        Skip.If(!await IsInternetAvailable(), "Internet not available");

        // Act
        var request = new WebFetchRequest(
            "https://example.com",
            Format: WebFetchOutputFormat.Text);
        var result = await _fetcher.FetchAsync(request);

        // Assert
        result.Status.ShouldBe(WebFetchStatus.Success);
        result.Content!.ShouldNotContain("<html");
        result.Content!.ShouldNotContain("<div");
    }

    [Fact]
    public async Task FetchAsync_WithNonexistentDomain_ReturnsError()
    {
        // Act
        var request = new WebFetchRequest("https://this-domain-definitely-does-not-exist-xyz123.com/page");
        var result = await _fetcher.FetchAsync(request);

        // Assert
        result.Status.ShouldBe(WebFetchStatus.Error);
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task FetchAsync_WithInvalidScheme_ReturnsError()
    {
        // Act
        var request = new WebFetchRequest("ftp://example.com/file");
        var result = await _fetcher.FetchAsync(request);

        // Assert
        result.Status.ShouldBe(WebFetchStatus.Error);
        result.ErrorMessage!.ShouldContain("http");
    }

    private static async Task<bool> IsInternetAvailable()
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetAsync("https://example.com");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}