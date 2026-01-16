using Agent.Modules;
using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Monitor;
using Infrastructure.Clients.Messaging;
using Infrastructure.Clients.ToolApproval;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Shouldly;
using StackExchange.Redis;

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
                ApiKey = "test-api-key"
            },
            Telegram = new TelegramConfiguration
            {
                AllowedUserNames = ["testuser"]
            },
            Redis = new RedisConfiguration
            {
                ConnectionString = "localhost:6379,abortConnect=false"
            },
            Agents =
            [
                new AgentDefinition
                {
                    Id = "test-agent",
                    Name = "TestAgent",
                    Model = "test-model",
                    McpServerEndpoints = ["http://localhost:5000"],
                    TelegramBotToken = "123456:ABC-DEF1234ghIkl-zyx57W2v1u123ew11"
                }
            ]
        };
    }

    private static void AddMockInfrastructure(IServiceCollection services)
    {
        // Pre-register mocks before ConfigureJack so they take precedence
        var mockMultiplexer = new Mock<IConnectionMultiplexer>();
        var mockDatabase = new Mock<IDatabase>();
        mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(mockDatabase.Object);

        // Remove existing registrations and add mocks
        var descriptorsToRemove = services
            .Where(d => d.ServiceType == typeof(IConnectionMultiplexer))
            .ToList();

        foreach (var descriptor in descriptorsToRemove)
        {
            services.Remove(descriptor);
        }

        services.AddSingleton(mockMultiplexer.Object);
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
        services.ConfigureAgents(settings, cmdParams);
        AddMockInfrastructure(services);
        var provider = services.BuildServiceProvider();

        // Assert - core services
        provider.GetService<IAgentFactory>().ShouldNotBeNull();
        provider.GetService<ChatMonitor>().ShouldNotBeNull();
        provider.GetService<AgentCleanupMonitor>().ShouldNotBeNull();
        provider.GetService<ChatThreadResolver>().ShouldNotBeNull();
        provider.GetService<IChatMessengerClient>().ShouldNotBeNull();
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
        services.ConfigureAgents(settings, cmdParams);
        AddMockInfrastructure(services);
        var provider = services.BuildServiceProvider();

        // Assert
        provider.GetService<ChatThreadResolver>().ShouldNotBeNull();
        provider.GetService<IChatMessengerClient>().ShouldNotBeNull();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        hostedServices.Length.ShouldBe(2);
    }

    [Fact]
    public void ConfigureJack_WithOneShotInterface_RegistersOneShotClient()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<IHostApplicationLifetime>().Object);
        var settings = CreateTestSettings();
        var cmdParams = new CommandLineParams
        {
            ChatInterface = ChatInterface.OneShot,
            Prompt = "Test prompt",
            ShowReasoning = true
        };

        // Act
        services.ConfigureAgents(settings, cmdParams);
        AddMockInfrastructure(services);
        var provider = services.BuildServiceProvider();

        // Assert
        provider.GetService<IAgentFactory>().ShouldNotBeNull();
        provider.GetService<ChatMonitor>().ShouldNotBeNull();

        var chatClient = provider.GetService<IChatMessengerClient>();
        chatClient.ShouldNotBeNull();
        chatClient.ShouldBeOfType<OneShotChatMessengerClient>();

        var approvalFactory = provider.GetService<IToolApprovalHandlerFactory>();
        approvalFactory.ShouldNotBeNull();
        approvalFactory.ShouldBeOfType<AutoApproveToolHandlerFactory>();
    }

    [Fact]
    public void ConfigureJack_WithOneShotInterfaceNoPrompt_ThrowsOnResolve()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<IHostApplicationLifetime>().Object);
        var settings = CreateTestSettings();
        var cmdParams = new CommandLineParams
        {
            ChatInterface = ChatInterface.OneShot,
            Prompt = null // Missing prompt
        };

        // Act
        services.ConfigureAgents(settings, cmdParams);
        AddMockInfrastructure(services);
        var provider = services.BuildServiceProvider();

        // Assert - Should throw when trying to resolve the client
        Should.Throw<InvalidOperationException>(() => provider.GetService<IChatMessengerClient>());
    }

    [Fact]
    public void GetCommandLineParams_WithChatOption_ParsesCorrectly()
    {
        // Act
        var result = ConfigModule.GetCommandLineParams(["--chat", "Cli"]);

        // Assert
        result.ChatInterface.ShouldBe(ChatInterface.Cli);
    }

    [Fact]
    public void GetCommandLineParams_WithPromptOption_SetsOneShotMode()
    {
        // Act
        var result = ConfigModule.GetCommandLineParams(["--prompt", "Hello world"]);

        // Assert
        result.ChatInterface.ShouldBe(ChatInterface.OneShot);
        result.Prompt.ShouldBe("Hello world");
        result.ShowReasoning.ShouldBeFalse();
    }

    [Fact]
    public void GetCommandLineParams_WithPromptShortOption_SetsOneShotMode()
    {
        // Act
        var result = ConfigModule.GetCommandLineParams(["-p", "Test prompt"]);

        // Assert
        result.ChatInterface.ShouldBe(ChatInterface.OneShot);
        result.Prompt.ShouldBe("Test prompt");
    }

    [Fact]
    public void GetCommandLineParams_WithReasoningOption_SetsShowReasoning()
    {
        // Act
        var result = ConfigModule.GetCommandLineParams(["--prompt", "Test", "--reasoning"]);

        // Assert
        result.ChatInterface.ShouldBe(ChatInterface.OneShot);
        result.Prompt.ShouldBe("Test");
        result.ShowReasoning.ShouldBeTrue();
    }

    [Fact]
    public void GetCommandLineParams_WithReasoningShortOption_SetsShowReasoning()
    {
        // Act
        var result = ConfigModule.GetCommandLineParams(["-p", "Test", "-r"]);

        // Assert
        result.ChatInterface.ShouldBe(ChatInterface.OneShot);
        result.ShowReasoning.ShouldBeTrue();
    }

    [Fact]
    public void GetCommandLineParams_WithPromptAndChatOption_PromptTakesPrecedence()
    {
        // Act - Even if --chat is specified, --prompt should override to OneShot
        var result = ConfigModule.GetCommandLineParams(["--chat", "Telegram", "--prompt", "Override"]);

        // Assert
        result.ChatInterface.ShouldBe(ChatInterface.OneShot);
        result.Prompt.ShouldBe("Override");
    }

    [Fact]
    public void GetCommandLineParams_WithNoOptions_DefaultsToTelegram()
    {
        // Act
        var result = ConfigModule.GetCommandLineParams([]);

        // Assert
        result.ChatInterface.ShouldBe(ChatInterface.Telegram);
        result.Prompt.ShouldBeNull();
        result.ShowReasoning.ShouldBeFalse();
    }
}