using Domain.Agents;
using Domain.Contracts;
using Domain.Monitor;
using Jack.Modules;
using Jack.Settings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Shouldly;

namespace Tests.Integration.Jack;

public class DependencyInjectionTests
{
    private static AgentSettings CreateTestSettings()
    {
        return new AgentSettings
        {
            OpenRouter = new OpenRouterConfiguration
            {
                ApiUrl = "https://openrouter.ai/api/v1/",
                ApiKey = "test-api-key",
                Models = ["test-model"]
            },
            Telegram = new TelegramConfiguration
            {
                BotToken = "123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11",
                AllowedUserNames = ["testuser"]
            },
            McpServers =
            [
                new Mcp
                {
                    Endpoint = "http://localhost:5000"
                }
            ],
            Redis = new RedisConfiguration
            {
                ConnectionString = "localhost:6379"
            }
        };
    }

    [Fact]
    public void ConfigureJack_WithCliInterface_RegistersAllServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<IHostApplicationLifetime>().Object);
        var settings = CreateTestSettings();
        var cmdParams = new CommandLineParams
        {
            ChatInterface = ChatInterface.Cli
        };

        // Act
        services.ConfigureJack(settings, cmdParams);
        var provider = services.BuildServiceProvider();

        // Assert - core services
        provider.GetService<IAgentFactory>().ShouldNotBeNull();
        provider.GetService<ChatMonitor>().ShouldNotBeNull();
        provider.GetService<AgentCleanupMonitor>().ShouldNotBeNull();
        provider.GetService<ChatThreadResolver>().ShouldNotBeNull();
        provider.GetService<IChatMessengerClient>().ShouldNotBeNull();
        provider.GetService<IChatClient>().ShouldNotBeNull();
    }

    [Fact]
    public void ConfigureJack_WithTelegramInterface_RegistersAllServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var settings = CreateTestSettings();
        var cmdParams = new CommandLineParams
        {
            ChatInterface = ChatInterface.Telegram
        };

        // Act
        services.ConfigureJack(settings, cmdParams);
        var provider = services.BuildServiceProvider();

        // Assert
        provider.GetService<ChatThreadResolver>().ShouldNotBeNull();
        provider.GetService<IChatMessengerClient>().ShouldNotBeNull();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        hostedServices.Length.ShouldBe(2);
    }

    [Fact]
    public void GetCommandLineParams_WithChatOption_ParsesCorrectly()
    {
        // Act
        var result = ConfigModule.GetCommandLineParams(["--chat", "Cli"]);

        // Assert
        result.ChatInterface.ShouldBe(ChatInterface.Cli);
    }
}