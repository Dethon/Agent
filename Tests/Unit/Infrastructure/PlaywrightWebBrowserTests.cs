using Domain.Contracts;
using Infrastructure.Clients;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class PlaywrightWebBrowserTests : IAsyncLifetime
{
    private PlaywrightWebBrowser _browser = null!;

    public Task InitializeAsync()
    {
        _browser = new PlaywrightWebBrowser();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
    }

    [Fact]
    public async Task NavigateAsync_WithInvalidUrl_ReturnsError()
    {
        // Act
        var request = new BrowseRequest(
            SessionId: "test",
            Url: "not-a-valid-url");
        var result = await _browser.NavigateAsync(request);

        // Assert
        result.Status.ShouldBe(BrowseStatus.Error);
        result.ErrorMessage!.ShouldContain("Invalid URL");
    }

    [Fact]
    public async Task NavigateAsync_WithFtpUrl_ReturnsError()
    {
        // Act
        var request = new BrowseRequest(
            SessionId: "test",
            Url: "ftp://example.com/file");
        var result = await _browser.NavigateAsync(request);

        // Assert
        result.Status.ShouldBe(BrowseStatus.Error);
        result.ErrorMessage!.ShouldContain("http");
    }

    [Fact]
    public async Task NavigateAsync_WithEmptyUrl_ReturnsError()
    {
        // Act
        var request = new BrowseRequest(
            SessionId: "test",
            Url: "");
        var result = await _browser.NavigateAsync(request);

        // Assert
        result.Status.ShouldBe(BrowseStatus.Error);
        result.ErrorMessage!.ShouldContain("Invalid URL");
    }

    [Fact]
    public async Task NavigateAsync_WithFileUrl_ReturnsError()
    {
        // Act
        var request = new BrowseRequest(
            SessionId: "test",
            Url: "file:///etc/passwd");
        var result = await _browser.NavigateAsync(request);

        // Assert
        result.Status.ShouldBe(BrowseStatus.Error);
        result.ErrorMessage!.ShouldContain("http");
    }

    [Fact]
    public async Task ClickAsync_WithNoSession_ReturnsSessionNotFound()
    {
        // Act
        var request = new ClickRequest(
            SessionId: "non-existent-session",
            Selector: "a");
        var result = await _browser.ClickAsync(request);

        // Assert
        result.Status.ShouldBe(ClickStatus.SessionNotFound);
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetCurrentPageAsync_WithNoSession_ReturnsSessionNotFound()
    {
        // Act
        var result = await _browser.GetCurrentPageAsync("non-existent-session");

        // Assert
        result.Status.ShouldBe(BrowseStatus.SessionNotFound);
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task CloseSessionAsync_WithNoSession_DoesNotThrow()
    {
        // Act & Assert - should not throw
        await _browser.CloseSessionAsync("non-existent-session");
    }
}