using Agent.Modules;
using Agent.Settings;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Clients.Messaging.WebChat;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration;

public sealed class InjectorModuleTests(RedisFixture redisFixture) : IClassFixture<RedisFixture>
{
    [Fact]
    public void AddWebClient_WithoutVapidConfig_UsesNullPushNotificationService()
    {
        var settings = CreateSettings(webPush: null);
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAgent(settings);
        services.AddChatMonitoring(settings, new CommandLineParams { ChatInterface = ChatInterface.Web });

        var provider = services.BuildServiceProvider();
        var pushService = provider.GetRequiredService<IPushNotificationService>();

        pushService.ShouldBeOfType<NullPushNotificationService>();
    }

    [Fact]
    public void AddWebClient_WithPartialVapidConfig_UsesNullPushNotificationService()
    {
        var settings = CreateSettings(new WebPushConfiguration
        {
            PublicKey = "BPublicKey",
            PrivateKey = null,
            Subject = "mailto:test@example.com"
        });

        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAgent(settings);
        services.AddChatMonitoring(settings, new CommandLineParams { ChatInterface = ChatInterface.Web });

        var provider = services.BuildServiceProvider();
        var pushService = provider.GetRequiredService<IPushNotificationService>();

        pushService.ShouldBeOfType<NullPushNotificationService>();
    }

    [Fact]
    public void AddWebClient_WithCompleteVapidConfig_UsesWebPushNotificationService()
    {
        var settings = CreateSettings(new WebPushConfiguration
        {
            PublicKey = "BPublicKey",
            PrivateKey = "PrivateKey",
            Subject = "mailto:test@example.com"
        });

        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAgent(settings);
        services.AddChatMonitoring(settings, new CommandLineParams { ChatInterface = ChatInterface.Web });

        var provider = services.BuildServiceProvider();
        var pushService = provider.GetRequiredService<IPushNotificationService>();

        pushService.ShouldBeOfType<WebPushNotificationService>();
    }

    private AgentSettings CreateSettings(WebPushConfiguration? webPush)
    {
        return new AgentSettings
        {
            OpenRouter = new OpenRouterConfiguration
            {
                ApiUrl = "https://test.openrouter.ai",
                ApiKey = "test-key"
            },
            Telegram = new TelegramConfiguration
            {
                AllowedUserNames = []
            },
            Redis = new RedisConfiguration
            {
                ConnectionString = redisFixture.ConnectionString
            },
            Agents =
            [
                new AgentDefinition
                {
                    Id = "test-agent",
                    Name = "Test Agent",
                    Description = "Test",
                    Model = "test-model",
                    McpServerEndpoints = []
                }
            ],
            WebPush = webPush
        };
    }
}
