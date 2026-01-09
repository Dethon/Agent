using Domain.Contracts;
using Infrastructure.Clients;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class PlaywrightWebFetcherTests : IAsyncLifetime
{
    private PlaywrightWebFetcher _fetcher = null!;

    public Task InitializeAsync()
    {
        _fetcher = new PlaywrightWebFetcher();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _fetcher.DisposeAsync();
    }

    [Fact]
    public async Task FetchAsync_WithInvalidUrl_ReturnsError()
    {
        // Act
        var request = new WebFetchRequest("not-a-valid-url");
        var result = await _fetcher.FetchAsync(request);

        // Assert
        result.Status.ShouldBe(WebFetchStatus.Error);
        result.ErrorMessage!.ShouldContain("Invalid URL");
    }

    [Fact]
    public async Task FetchAsync_WithFtpUrl_ReturnsError()
    {
        // Act
        var request = new WebFetchRequest("ftp://example.com/file");
        var result = await _fetcher.FetchAsync(request);

        // Assert
        result.Status.ShouldBe(WebFetchStatus.Error);
        result.ErrorMessage!.ShouldContain("http");
    }

    [Fact]
    public async Task FetchAsync_WithEmptyUrl_ReturnsError()
    {
        // Act
        var request = new WebFetchRequest("");
        var result = await _fetcher.FetchAsync(request);

        // Assert
        result.Status.ShouldBe(WebFetchStatus.Error);
        result.ErrorMessage!.ShouldContain("Invalid URL");
    }

    [Fact]
    public async Task FetchAsync_WithFileUrl_ReturnsError()
    {
        // Act
        var request = new WebFetchRequest("file:///etc/passwd");
        var result = await _fetcher.FetchAsync(request);

        // Assert
        result.Status.ShouldBe(WebFetchStatus.Error);
        result.ErrorMessage!.ShouldContain("http");
    }
}