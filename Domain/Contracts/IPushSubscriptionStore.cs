using Domain.DTOs.WebChat;

namespace Domain.Contracts;

public interface IPushSubscriptionStore
{
    Task SaveAsync(string userId, PushSubscriptionDto subscription, string spaceSlug = "default", string? replacingEndpoint = null, CancellationToken ct = default);
    Task RemoveAsync(string userId, string endpoint, CancellationToken ct = default);
    Task RemoveByEndpointAsync(string endpoint, CancellationToken ct = default);
    Task<IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)>> GetBySpaceAsync(string spaceSlug, CancellationToken ct = default);
}
