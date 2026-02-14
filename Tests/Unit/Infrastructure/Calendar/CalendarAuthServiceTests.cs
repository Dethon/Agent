using System.Text;
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

    [Fact]
    public void GetAuthorizationUrl_StateParameter_DecodesToOriginalUserId()
    {
        var url = _service.GetAuthorizationUrl("user-1", "https://example.com/callback");

        var uri = new Uri(url);
        var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var state = queryParams["state"]!;
        var decodedUserId = Encoding.UTF8.GetString(Convert.FromBase64String(state));

        decodedUserId.ShouldBe("user-1");
    }

    [Fact]
    public void GetAuthorizationUrl_ContainsTenantIdInPath()
    {
        var url = _service.GetAuthorizationUrl("user-1", "https://example.com/callback");

        var uri = new Uri(url);
        uri.AbsolutePath.ShouldContain("test-tenant-id");
    }

    [Fact]
    public void GetAuthorizationUrl_IncludesOfflineAccessScope()
    {
        var url = _service.GetAuthorizationUrl("user-1", "https://example.com/callback");

        var uri = new Uri(url);
        var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var scope = queryParams["scope"]!;

        scope.ShouldContain("offline_access");
    }

    [Fact]
    public void GetAuthorizationUrl_IncludesOpenIdScope()
    {
        var url = _service.GetAuthorizationUrl("user-1", "https://example.com/callback");

        var uri = new Uri(url);
        var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var scope = queryParams["scope"]!;

        scope.ShouldContain("openid");
    }

    [Fact]
    public void GetAuthorizationUrl_RedirectUriIsCorrectlyEncoded()
    {
        var redirectUri = "https://example.com/callback?foo=bar&baz=qux";
        var url = _service.GetAuthorizationUrl("user-1", redirectUri);

        var uri = new Uri(url);
        var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var parsedRedirectUri = queryParams["redirect_uri"]!;

        parsedRedirectUri.ShouldBe(redirectUri);
    }

    [Fact]
    public void GetAuthorizationUrl_ProducesValidUri()
    {
        var url = _service.GetAuthorizationUrl("user-1", "https://example.com/callback");

        var isValid = Uri.TryCreate(url, UriKind.Absolute, out var uri);
        isValid.ShouldBeTrue();
        uri!.Scheme.ShouldBe("https");
    }

    [Fact]
    public void GetAuthorizationUrl_DifferentUsersProduceDifferentState()
    {
        var url1 = _service.GetAuthorizationUrl("user-1", "https://example.com/callback");
        var url2 = _service.GetAuthorizationUrl("user-2", "https://example.com/callback");

        var uri1 = new Uri(url1);
        var uri2 = new Uri(url2);
        var state1 = System.Web.HttpUtility.ParseQueryString(uri1.Query)["state"];
        var state2 = System.Web.HttpUtility.ParseQueryString(uri2.Query)["state"];

        state1.ShouldNotBe(state2);
    }

    [Fact]
    public async Task GetStatusAsync_PassesCorrectUserIdToTokenStore()
    {
        _tokenStoreMock.Setup(s => s.HasTokensAsync("specific-user-42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _service.GetStatusAsync("specific-user-42");

        result.Connected.ShouldBeTrue();
        _tokenStoreMock.Verify(s => s.HasTokensAsync("specific-user-42", It.IsAny<CancellationToken>()), Times.Once);
    }
}
