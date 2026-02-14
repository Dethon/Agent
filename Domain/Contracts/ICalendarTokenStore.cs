using Domain.DTOs;

namespace Domain.Contracts;

public interface ICalendarTokenStore
{
    Task<OAuthTokens?> GetTokensAsync(string userId, CancellationToken ct = default);
    Task StoreTokensAsync(string userId, OAuthTokens tokens, CancellationToken ct = default);
    Task RemoveTokensAsync(string userId, CancellationToken ct = default);
    Task<bool> HasTokensAsync(string userId, CancellationToken ct = default);
}
