using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.ConnectedAccounts;

namespace Tests.Unit.WebChat.Client.State;

public class ConnectedAccountsStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ConnectedAccountsStore _store;

    public ConnectedAccountsStoreTests()
    {
        _dispatcher = new Dispatcher();
        _store = new ConnectedAccountsStore(_dispatcher);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void Initial_HasNoProviders()
    {
        _store.State.Providers.ShouldBeEmpty();
    }

    [Fact]
    public void AccountStatusLoaded_SetsProviderStatus()
    {
        _dispatcher.Dispatch(new AccountStatusLoaded("microsoft", true, "user@example.com"));

        _store.State.Providers.ShouldContainKey("microsoft");
        _store.State.Providers["microsoft"].Connected.ShouldBeTrue();
        _store.State.Providers["microsoft"].Email.ShouldBe("user@example.com");
    }

    [Fact]
    public void AccountStatusLoaded_WhenNotConnected_SetsNotConnected()
    {
        _dispatcher.Dispatch(new AccountStatusLoaded("microsoft", false, null));

        _store.State.Providers.ShouldContainKey("microsoft");
        _store.State.Providers["microsoft"].Connected.ShouldBeFalse();
        _store.State.Providers["microsoft"].Email.ShouldBeNull();
    }

    [Fact]
    public void AccountConnected_UpdatesProviderStatus()
    {
        _dispatcher.Dispatch(new AccountConnected("microsoft", "user@example.com"));

        _store.State.Providers.ShouldContainKey("microsoft");
        _store.State.Providers["microsoft"].Connected.ShouldBeTrue();
        _store.State.Providers["microsoft"].Email.ShouldBe("user@example.com");
    }

    [Fact]
    public void AccountDisconnected_ClearsProviderStatus()
    {
        _dispatcher.Dispatch(new AccountConnected("microsoft", "user@example.com"));
        _dispatcher.Dispatch(new AccountDisconnected("microsoft"));

        _store.State.Providers.ShouldContainKey("microsoft");
        _store.State.Providers["microsoft"].Connected.ShouldBeFalse();
    }

    [Fact]
    public void AccountDisconnected_ClearsEmail()
    {
        _dispatcher.Dispatch(new AccountConnected("microsoft", "user@example.com"));
        _dispatcher.Dispatch(new AccountDisconnected("microsoft"));

        _store.State.Providers["microsoft"].Email.ShouldBeNull();
    }

    [Fact]
    public void MultipleProviders_Independent()
    {
        _dispatcher.Dispatch(new AccountStatusLoaded("microsoft", true, "ms@example.com"));
        _dispatcher.Dispatch(new AccountStatusLoaded("google", true, "g@example.com"));

        _store.State.Providers.Count.ShouldBe(2);
        _store.State.Providers["microsoft"].Email.ShouldBe("ms@example.com");
        _store.State.Providers["google"].Email.ShouldBe("g@example.com");
    }

    [Fact]
    public void AccountConnected_OverwritesExistingEmail()
    {
        _dispatcher.Dispatch(new AccountConnected("microsoft", "old@example.com"));
        _dispatcher.Dispatch(new AccountConnected("microsoft", "new@example.com"));

        _store.State.Providers["microsoft"].Email.ShouldBe("new@example.com");
    }

    [Fact]
    public void StateObservable_EmitsOnDispatch()
    {
        var emissions = new List<ConnectedAccountsState>();
        using var subscription = _store.StateObservable.Subscribe(emissions.Add);

        _dispatcher.Dispatch(new AccountStatusLoaded("microsoft", true, "user@example.com"));

        emissions.Count.ShouldBe(2); // Initial + AccountStatusLoaded
        emissions[1].Providers.ShouldContainKey("microsoft");
    }

    [Fact]
    public void StateObservable_EmitsCurrentStateOnSubscription()
    {
        _dispatcher.Dispatch(new AccountStatusLoaded("microsoft", true, "user@example.com"));

        ConnectedAccountsState? receivedState = null;
        using var subscription = _store.StateObservable.Subscribe(state => receivedState = state);

        receivedState.ShouldNotBeNull();
        receivedState.Providers.ShouldContainKey("microsoft");
        receivedState.Providers["microsoft"].Connected.ShouldBeTrue();
    }

    [Fact]
    public void AccountDisconnected_NonExistentProvider_DoesNotThrow()
    {
        Should.NotThrow(() => _dispatcher.Dispatch(new AccountDisconnected("nonexistent")));

        _store.State.Providers.ShouldContainKey("nonexistent");
        _store.State.Providers["nonexistent"].Connected.ShouldBeFalse();
    }

    [Fact]
    public void Reducer_DoesNotMutateOriginalStateDictionary()
    {
        _dispatcher.Dispatch(new AccountStatusLoaded("microsoft", true, "ms@example.com"));

        var stateBeforeSecondDispatch = _store.State;
        var providersBefore = stateBeforeSecondDispatch.Providers;

        _dispatcher.Dispatch(new AccountStatusLoaded("google", true, "g@example.com"));

        providersBefore.Count.ShouldBe(1);
        providersBefore.ShouldContainKey("microsoft");
        providersBefore.ShouldNotContainKey("google");

        _store.State.Providers.Count.ShouldBe(2);
    }

    [Fact]
    public void AccountStatusLoaded_NotConnected_CreatesProviderEntry()
    {
        _dispatcher.Dispatch(new AccountStatusLoaded("microsoft", false, null));

        _store.State.Providers.ShouldContainKey("microsoft");
        _store.State.Providers["microsoft"].Connected.ShouldBeFalse();
        _store.State.Providers["microsoft"].Email.ShouldBeNull();
    }
}
