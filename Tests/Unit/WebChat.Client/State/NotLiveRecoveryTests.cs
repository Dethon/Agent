using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Contracts;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client.State;

// A call the client made for its own reasons says nothing when it could not be made. The user
// asked for none of them and can do nothing about them; they are retried on becoming live.
public sealed class NotLiveRecoveryTests
{
    [Fact]
    public async Task ASpaceJoin_ThatCouldNotBeMade_SaysNothing()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        client.ConfigService.WithSpace("hearth");

        client.GoNotLive();
        await client.Service<SpaceEffect>().HandleSelectSpaceAsync("hearth");

        client.Toasts.State.Toasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task AUserRegistration_ThatCouldNotBeMade_SaysNothing()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        client.Dispatcher.Dispatch(new SelectUser("user-1"));

        client.GoNotLive();
        await client.Service<InitializationEffect>().RegisterUserAsync("user-1");

        client.Toasts.State.Toasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task AUserRegistration_WhileLive_StillReachesTheServer()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();

        await client.Service<IChatSessionService>().RegisterUserAsync("user-1");

        var call = transport.Calls.Single(c => c.MethodName == "RegisterUser");
        call.Arguments.ShouldBe(["user-1"]);
    }

    [Fact]
    public async Task SessionRecovery_WhileNotLive_RaisesNoToastAndDisturbsNoStore()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        client.Dispatcher.Dispatch(new SelectUser("user-1"));
        client.Dispatcher.Dispatch(new TopicsLoaded([]));

        client.GoNotLive();
        await client.Service<ISessionRecovery>().RecoverAsync();

        client.Toasts.State.Toasts.ShouldBeEmpty();
        client.Topics.State.Topics.ShouldBeEmpty();
        client.UserIdentity.State.SelectedUserId.ShouldBe("user-1");
    }

    [Fact]
    public async Task SessionRecovery_WhileLive_ReIdentifiesTheUserAndRejoinsTheSpace()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        client.Dispatcher.Dispatch(new SelectUser("user-1"));

        await client.Service<ISessionRecovery>().RecoverAsync();

        transport.Calls.ShouldContain(call => call.MethodName == "RegisterUser");
        transport.Calls.ShouldContain(call => call.MethodName == "JoinSpace");
    }
}