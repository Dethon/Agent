using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakePushSubscriptionService : IPushSubscriptionService
{
    private readonly TaskCompletionSource _gate = new();
    private readonly TaskCompletionSource _subscribeCalled = new();

    public bool BlockUntilReleased { get; set; }

    public Exception? ThrowOnResubscribe { get; set; }

    public string? SubscribedVapidKey { get; private set; }

    public int ResubscribeCalls { get; private set; }

    public Task SubscribeCalled => _subscribeCalled.Task;

    public void Release() => _gate.TrySetResult();

    public async Task<bool> RequestAndSubscribeAsync(string vapidPublicKey)
    {
        SubscribedVapidKey = vapidPublicKey;
        _subscribeCalled.TrySetResult();

        if (BlockUntilReleased)
        {
            await _gate.Task;
        }

        return true;
    }

    public Task ResubscribeAsync()
    {
        ResubscribeCalls++;
        return ThrowOnResubscribe is null ? Task.CompletedTask : Task.FromException(ThrowOnResubscribe);
    }

    public Task UnsubscribeAsync() => Task.CompletedTask;

    public Task<bool> IsSubscribedAsync() => Task.FromResult(SubscribedVapidKey is not null);
}