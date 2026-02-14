using System.Text;
using Domain.Contracts;

namespace Infrastructure.Calendar;

public class CalendarAuthService(ICalendarTokenStore tokenStore, CalendarAuthSettings settings)
{
    private const string AuthorizeEndpoint = "https://login.microsoftonline.com/{0}/oauth2/v2.0/authorize";
    private const string CalendarScope = "Calendars.ReadWrite";

    public string GetAuthorizationUrl(string userId, string redirectUri)
    {
        var baseUrl = string.Format(AuthorizeEndpoint, settings.TenantId);
        var state = Convert.ToBase64String(Encoding.UTF8.GetBytes(userId));

        var queryParams = new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = $"openid offline_access {CalendarScope}",
            ["state"] = state,
            ["response_mode"] = "query"
        };

        var queryString = string.Join("&", queryParams.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

        return $"{baseUrl}?{queryString}";
    }

    public async Task<CalendarAuthStatus> GetStatusAsync(string userId, CancellationToken ct = default)
    {
        var hasTokens = await tokenStore.HasTokensAsync(userId, ct);
        return new CalendarAuthStatus(hasTokens);
    }

    public async Task DisconnectAsync(string userId, CancellationToken ct = default)
    {
        await tokenStore.RemoveTokensAsync(userId, ct);
    }
}
