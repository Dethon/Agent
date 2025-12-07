using Domain.Agents;
using Domain.Contracts;
using Domain.Monitor;
using Jack.Modules;
using Jack.Settings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
            ]
        };
    }

    [Fact]
    public void ConfigureJack_WithCliInterface_RegistersAllServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var settings = CreateTestSettings();
        var cmdParams = new CommandLineParams
        {
            WorkersCount = 2,
            ChatInterface = ChatInterface.Cli
        };

        // Act
        services.ConfigureJack(settings, cmdParams);
        var provider = services.BuildServiceProvider();

        // Assert - core services
        provider.GetService<Func<string, CancellationToken, Task<DisposableAgent>>>().ShouldNotBeNull();
        provider.GetService<TaskQueue>().ShouldNotBeNull();
        provider.GetService<ChatMonitor>().ShouldNotBeNull();
        provider.GetService<AgentCleanupMonitor>().ShouldNotBeNull();
        provider.GetService<ThreadResolver>().ShouldNotBeNull();
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
            WorkersCount = 2,
            ChatInterface = ChatInterface.Telegram
        };

        // Act
        services.ConfigureJack(settings, cmdParams);
        var provider = services.BuildServiceProvider();

        // Assert
        provider.GetService<ThreadResolver>().ShouldNotBeNull();
        provider.GetService<IChatMessengerClient>().ShouldNotBeNull();
    }

    [Fact]
    public void ConfigureJack_WithWorkersCount_RegistersCorrectNumberOfTaskRunners()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var settings = CreateTestSettings();
        var cmdParams = new CommandLineParams
        {
            WorkersCount = 3,
            ChatInterface = ChatInterface.Cli
        };

        // Act
        services.ConfigureJack(settings, cmdParams);
        var provider = services.BuildServiceProvider();

        // Assert - should have 3 TaskRunner hosted services + 2 monitoring services
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        hostedServices.Length.ShouldBe(5); // 3 TaskRunners + ChatMonitoring + CleanupMonitoring
    }

    [Fact]
    public async Task ConfigureJack_TaskQueueCapacity_IsDoubleWorkersCount()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var settings = CreateTestSettings();
        var cmdParams = new CommandLineParams
        {
            WorkersCount = 5,
            ChatInterface = ChatInterface.Cli
        };

        // Act
        services.ConfigureJack(settings, cmdParams);
        var provider = services.BuildServiceProvider();

        // Assert - TaskQueue capacity should be workers * 2 = 10
        var queue = provider.GetRequiredService<TaskQueue>();

        // Fill the queue to test capacity
        for (var i = 0; i < 10; i++)
        {
            await queue.QueueTask(_ => Task.CompletedTask);
        }

        // 11th item should block (not complete immediately)
        var eleventhTask = queue.QueueTask(_ => Task.CompletedTask);
        eleventhTask.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public void GetCommandLineParams_WithDefaultArgs_ReturnsDefaults()
    {
        // Act
        var result = ConfigModule.GetCommandLineParams([]);

        // Assert
        result.WorkersCount.ShouldBe(10);
        result.ChatInterface.ShouldBe(ChatInterface.Telegram);
    }

    [Fact]
    public void GetCommandLineParams_WithWorkerOption_ParsesCorrectly()
    {
        // Act
        var result = ConfigModule.GetCommandLineParams(["--workers", "5"]);

        // Assert
        result.WorkersCount.ShouldBe(5);
    }

    [Fact]
    public void GetCommandLineParams_WithShortWorkerOption_ParsesCorrectly()
    {
        // Act
        var result = ConfigModule.GetCommandLineParams(["-w", "3"]);

        // Assert
        result.WorkersCount.ShouldBe(3);
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
    public void GetCommandLineParams_WithAllOptions_ParsesCorrectly()
    {
        // Act
        var result = ConfigModule.GetCommandLineParams(["-w", "7", "--chat", "Cli"]);

        // Assert
        result.WorkersCount.ShouldBe(7);
        result.ChatInterface.ShouldBe(ChatInterface.Cli);
    }
}