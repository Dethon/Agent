using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Services;
using WebChat.Client.State.Hub;

namespace Tests.Unit.WebChat.Client.Extensions;

// Session recovery resolves back through the live connection it is injected into, so the
// registration is one Lazy away from a container cycle that nothing else would catch.
public sealed class WebChatLiveConnectionRegistrationTests
{
    [Fact]
    public async Task AddWebChatLiveConnection_ResolvesTheLiveConnection()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();

        Should.NotThrow(() => scope.ServiceProvider.GetRequiredService<IChatLiveConnection>());
    }

    [Fact]
    public async Task AddWebChatLiveConnection_ResolvesSessionRecoveryThroughTheSameLiveConnection()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var liveConnection = scope.ServiceProvider.GetRequiredService<ChatLiveConnection>();

        Should.NotThrow(() => scope.ServiceProvider.GetRequiredService<ISessionRecovery>());

        scope.ServiceProvider.GetRequiredService<IChatLiveConnection>().ShouldBeSameAs(liveConnection);
    }

    [Fact]
    public async Task AddWebChatLiveConnection_TheLazyRecoveryResolvesAfterTheLiveConnectionIsBuilt()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IChatLiveConnection>();

        var lazy = scope.ServiceProvider.GetRequiredService<Lazy<ISessionRecovery>>();

        Should.NotThrow(() => lazy.Value);
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new HttpClient { BaseAddress = new Uri("https://localhost/") });
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<NavigationManager>(_ => new FakeNavigationManager());
        services.AddScoped(_ => new Mock<IHubEventDispatcher>().Object);
        services.AddScoped<ConnectionEventDispatcher>();
        services.AddScoped<ITopicService>(_ => new FakeTopicService());
        services.AddScoped<IPushSubscriptionService>(_ => new FakePushSubscriptionService());
        services.AddWebChatStores();
        services.AddWebChatLiveConnection();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}