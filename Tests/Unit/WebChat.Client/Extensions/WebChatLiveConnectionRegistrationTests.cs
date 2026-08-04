using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Services;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Hub;
using WebChat.Client.State.Pipeline;

namespace Tests.Unit.WebChat.Client.Extensions;

// The live connection sits underneath everything that makes a hub call, and both the hub
// event binder and session recovery reach back down to it — so the registration is two Lazys
// away from a container cycle, and a cycle here is a blank page rather than a failing test.
// Faking a collaborator would fake away the edge under test, so everything the client
// registers is real except the browser primitives.
public sealed class WebChatLiveConnectionRegistrationTests
{
    [Fact]
    public async Task TheClientRegistrations_ResolveTheLiveConnection()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();

        Should.NotThrow(() => scope.ServiceProvider.GetRequiredService<IChatLiveConnection>());
    }

    [Fact]
    public async Task TheClientRegistrations_ResolveTheHubEventBinder()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();

        Should.NotThrow(() => scope.ServiceProvider.GetRequiredService<IHubEventBinder>());
    }

    [Fact]
    public async Task TheClientRegistrations_ResolveSessionRecovery()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();

        Should.NotThrow(() => scope.ServiceProvider.GetRequiredService<ISessionRecovery>());
    }

    // Program.cs activates these at start-up, so a cycle under any of them stops the app.
    [Theory]
    [InlineData(typeof(ReconnectionEffect))]
    [InlineData(typeof(SendMessageEffect))]
    [InlineData(typeof(TopicSelectionEffect))]
    [InlineData(typeof(TopicDeleteEffect))]
    [InlineData(typeof(InitializationEffect))]
    [InlineData(typeof(AgentSelectionEffect))]
    [InlineData(typeof(UserIdentityEffect))]
    [InlineData(typeof(SpaceEffect))]
    [InlineData(typeof(AgentActivityEffect))]
    [InlineData(typeof(AgentSettingsEffect))]
    public async Task TheClientRegistrations_ResolveEveryStartUpEffect(Type effectType)
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();

        Should.NotThrow(() => scope.ServiceProvider.GetRequiredService(effectType));
    }

    [Fact]
    public async Task TheLiveConnection_IsTheSameInstanceItsCollaboratorsReachBackTo()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var liveConnection = scope.ServiceProvider.GetRequiredService<ChatLiveConnection>();

        scope.ServiceProvider.GetRequiredService<ISessionRecovery>();
        scope.ServiceProvider.GetRequiredService<IHubEventBinder>();

        scope.ServiceProvider.GetRequiredService<IChatLiveConnection>().ShouldBeSameAs(liveConnection);
    }

    // Mirrors Program.cs. Only the browser primitives are substituted.
    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => new HttpClient { BaseAddress = new Uri("https://localhost/") });
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<NavigationManager>(_ => new FakeNavigationManager());
        services.AddScoped(_ => new Mock<IJSRuntime>().Object);
        services.AddScoped<ILocalStorageService>(_ => new FakeLocalStorageService());

        services.AddScoped<IHubEventDispatcher, HubEventDispatcher>();
        services.AddScoped<ConnectionEventDispatcher>();

        services.AddWebChatLiveConnection();

        services.AddScoped<IChatSessionService, ChatSessionService>();
        services.AddScoped<IChatMessagingService, ChatMessagingService>();
        services.AddScoped<ITopicService, TopicService>();
        services.AddScoped<IAgentService, AgentService>();
        services.AddScoped<IApprovalService, ApprovalService>();

        services.AddWebChatStores();
        services.AddWebChatEffects();

        services.AddScoped<IStreamingService, StreamingService>();
        services.AddScoped<StreamResumeService>();
        services.AddScoped<IStreamResumeService>(sp => sp.GetRequiredService<StreamResumeService>());
        services.AddScoped<IMessagePipeline, MessagePipeline>();

        services.AddScoped<PushNotificationService>();
        services.AddScoped<IPushSubscriptionService>(sp => sp.GetRequiredService<PushNotificationService>());

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}