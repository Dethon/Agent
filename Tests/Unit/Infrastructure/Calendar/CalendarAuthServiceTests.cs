using Domain.Contracts;
using Infrastructure.Calendar;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Calendar;

public class CalendarAuthServiceTests
{
    private readonly Mock<ICalendarTokenStore> _tokenStoreMock = new();
    private readonly CalendarAuthService _service;

    public CalendarAuthServiceTests()
    {
        var settings = new CalendarAuthSettings
        {
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret",
            TenantId = "test-tenant-id"
        };
        _service = new CalendarAuthService(_tokenStoreMock.Object, settings);
    }

    [Fact]
    public void GetAuthorizationUrl_ContainsMicrosoftLoginDomain()
    {
        var url = _service.GetAuthorizationUrl("user-1", "https://example.com/callback");

        url.ShouldContain("login.microsoftonline.com");
    }

    [Fact]
    public void GetAuthorizationUrl_ContainsCalendarScope()
    {
        var url = _service.GetAuthorizationUrl("user-1", "https://example.com/callback");

        url.ShouldContain("Calendars.ReadWrite");
    }

    [Fact]
    public void GetAuthorizationUrl_ContainsClientId()
    {
        var url = _service.GetAuthorizationUrl("user-1", "https://example.com/callback");

        url.ShouldContain("test-client-id");
    }

    [Fact]
    public void GetAuthorizationUrl_ContainsStateWithUserId()
    {
        var url = _service.GetAuthorizationUrl("user-1", "https://example.com/callback");

        url.ShouldContain("state=");
    }

    [Fact]
    public void GetAuthorizationUrl_ContainsRedirectUri()
    {
        var url = _service.GetAuthorizationUrl("user-1", "https://example.com/callback");

        url.ShouldContain("redirect_uri=");
    }

    [Fact]
    public void GetAuthorizationUrl_ContainsCodeResponseType()
    {
        var url = _service.GetAuthorizationUrl("user-1", "https://example.com/callback");

        url.ShouldContain("response_type=code");
    }

    [Fact]
    public async Task GetStatusAsync_WhenTokensExist_ReturnsConnected()
    {
        _tokenStoreMock.Setup(s => s.HasTokensAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _service.GetStatusAsync("user-1");

        result.Connected.ShouldBeTrue();
    }

    [Fact]
    public async Task GetStatusAsync_WhenNoTokens_ReturnsNotConnected()
    {
        _tokenStoreMock.Setup(s => s.HasTokensAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _service.GetStatusAsync("user-1");

        result.Connected.ShouldBeFalse();
    }

    [Fact]
    public async Task DisconnectAsync_RemovesTokensFromStore()
    {
        await _service.DisconnectAsync("user-1");

        _tokenStoreMock.Verify(s => s.RemoveTokensAsync("user-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DisconnectAsync_ForNonExistentUser_DoesNotThrow()
    {
        await Should.NotThrowAsync(() => _service.DisconnectAsync("nonexistent-user"));
    }
}
