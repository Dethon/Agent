using WebChat.Client.Contracts;
using WebChat.Client.State.Space;
using WebChat.Client.State.UserIdentity;

namespace WebChat.Client.Services;

public sealed class SessionRecovery(
    IChatSessionService sessionService,
    ITopicService topicService,
    IPushSubscriptionService pushSubscriptionService,
    UserIdentityStore userIdentityStore,
    SpaceStore spaceStore) : ISessionRecovery
{
    public Task RecoverAsync()
    {
        var registerTask = RegisterUserAsync();
        var joinTask = topicService.JoinSpaceAsync(spaceStore.State.CurrentSlug);

        return Task.WhenAll(registerTask, joinTask, ResubscribePushAsync());
    }

    // Re-sends the existing push subscription without force-refreshing the push channel.
    // A full resubscribe would generate a new endpoint in Chrome and lose the space
    // memberships attached to the old one.
    private async Task ResubscribePushAsync()
    {
        try
        {
            await pushSubscriptionService.ResubscribeAsync();
        }
        catch
        {
            // Best-effort — a browser that has dropped its subscription must not fail recovery
        }
    }

    private Task RegisterUserAsync()
    {
        var userId = userIdentityStore.State.SelectedUserId;
        return string.IsNullOrEmpty(userId) ? Task.CompletedTask : sessionService.RegisterUserAsync(userId);
    }
}