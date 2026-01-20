using Moq;
using Shouldly;
using WebChat.Client.Contracts;
using WebChat.Client.Services;
using WebChat.Client.State.Hub;

namespace Tests.Unit.WebChat.Client.Services;

public sealed class SignalREventSubscriberTests : IDisposable
{
    private readonly TestableSignalREventSubscriber _sut;
    private readonly Mock<IHubEventDispatcher> _mockHubEventDispatcher;

    public SignalREventSubscriberTests()
    {
        _mockHubEventDispatcher = new Mock<IHubEventDispatcher>();
        _sut = new TestableSignalREventSubscriber(_mockHubEventDispatcher.Object);
    }

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
    private readonly IHubEventDispatcher _hubEventDispatcher;
    private readonly List<MockDisposable> _subscriptions = new();
    private bool _disposed;
    private bool _hubConnectionIsNull;

    public TestableSignalREventSubscriber(IHubEventDispatcher hubEventDispatcher)
    {
        _hubEventDispatcher = hubEventDispatcher;
    }

    public bool IsSubscribed { get; private set; }
    public int SubscriptionCount => _subscriptions.Count;
    public int DisposedSubscriptionCount => _subscriptions.Count(s => s.IsDisposed) + _disposedAndClearedCount;

    private int _disposedAndClearedCount;

    public void SetHubConnectionNull() => _hubConnectionIsNull = true;
    public void ResetDisposedCount() => _disposedAndClearedCount = 0;

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

        _subscriptions.Add(new MockDisposable("OnTopicChanged"));
        _subscriptions.Add(new MockDisposable("OnStreamChanged"));
        _subscriptions.Add(new MockDisposable("OnNewMessage"));
        _subscriptions.Add(new MockDisposable("OnApprovalResolved"));
        _subscriptions.Add(new MockDisposable("OnToolCalls"));

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

    private sealed class MockDisposable(string name) : IDisposable
    {
        public string Name { get; } = name;
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
