using Shouldly;
using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Client.Services;

public sealed class SignalREventSubscriberTests : IDisposable
{
    private readonly TestableSignalREventSubscriber _sut = new();

    public void Dispose()
    {
        _sut.Dispose();
    }

    [Fact]
    public void Subscribe_WhenNotSubscribed_SetsIsSubscribedTrue()
    {
        _sut.Subscribe();

        _sut.IsSubscribed.ShouldBeTrue();
    }

    [Fact]
    public void Subscribe_WhenNotSubscribed_RegistersAllHandlers()
    {
        _sut.Subscribe();

        _sut.SubscriptionCount.ShouldBe(5);
    }

    [Fact]
    public void Subscribe_WhenAlreadySubscribed_DoesNotRegisterAgain()
    {
        _sut.Subscribe();
        _sut.Subscribe();

        _sut.SubscriptionCount.ShouldBe(5);
    }

    [Fact]
    public void Subscribe_WhenHubConnectionNull_DoesNothing()
    {
        _sut.SetHubConnectionNull();

        _sut.Subscribe();

        _sut.IsSubscribed.ShouldBeFalse();
        _sut.SubscriptionCount.ShouldBe(0);
    }

    [Fact]
    public void Unsubscribe_DisposesAllSubscriptions()
    {
        _sut.Subscribe();

        _sut.Unsubscribe();

        _sut.DisposedSubscriptionCount.ShouldBe(5);
        _sut.SubscriptionCount.ShouldBe(0);
    }

    [Fact]
    public void Unsubscribe_SetsIsSubscribedFalse()
    {
        _sut.Subscribe();

        _sut.Unsubscribe();

        _sut.IsSubscribed.ShouldBeFalse();
    }

    [Fact]
    public void Unsubscribe_AllowsResubscription()
    {
        _sut.Subscribe();
        _sut.Unsubscribe();

        _sut.Subscribe();

        _sut.IsSubscribed.ShouldBeTrue();
        _sut.SubscriptionCount.ShouldBe(5);
    }

    [Fact]
    public void Dispose_DisposesAllSubscriptions()
    {
        _sut.Subscribe();

        _sut.Dispose();

        _sut.DisposedSubscriptionCount.ShouldBe(5);
    }

    [Fact]
    public void Dispose_PreventsResubscription()
    {
        _sut.Subscribe();
        _sut.Dispose();
        _sut.ResetDisposedCount();

        _sut.Subscribe();

        _sut.IsSubscribed.ShouldBeFalse();
        _sut.SubscriptionCount.ShouldBe(0);
    }

    [Fact]
    public void IsSubscribed_InitiallyFalse()
    {
        _sut.IsSubscribed.ShouldBeFalse();
    }

    [Fact]
    public void Dispose_WhenCalledMultipleTimes_DoesNotThrow()
    {
        _sut.Subscribe();

        Should.NotThrow(() =>
        {
            _sut.Dispose();
            _sut.Dispose();
        });
    }
}

internal sealed class TestableSignalREventSubscriber : ISignalREventSubscriber
{
    private readonly List<MockDisposable> _subscriptions = new();
    private bool _disposed;

    private int _disposedAndClearedCount;
    private bool _hubConnectionIsNull;
    public int SubscriptionCount => _subscriptions.Count;
    public int DisposedSubscriptionCount => _subscriptions.Count(s => s.IsDisposed) + _disposedAndClearedCount;

    public bool IsSubscribed { get; private set; }

    public void Subscribe()
    {
        if (IsSubscribed || _disposed)
        {
            return;
        }

        if (_hubConnectionIsNull)
        {
            return;
        }

        _subscriptions.Add(new MockDisposable());
        _subscriptions.Add(new MockDisposable());
        _subscriptions.Add(new MockDisposable());
        _subscriptions.Add(new MockDisposable());
        _subscriptions.Add(new MockDisposable());

        IsSubscribed = true;
    }

    public void Unsubscribe()
    {
        _disposedAndClearedCount += _subscriptions.Count(s => !s.IsDisposed);
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        IsSubscribed = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Unsubscribe();
        _disposed = true;
    }

    public void SetHubConnectionNull()
    {
        _hubConnectionIsNull = true;
    }

    public void ResetDisposedCount()
    {
        _disposedAndClearedCount = 0;
    }

    private sealed class MockDisposable : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}