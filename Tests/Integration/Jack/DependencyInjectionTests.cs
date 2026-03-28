using System.Net;
using Agent.Modules;
using Microsoft.Extensions.Configuration;
using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Monitor;
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
                    McpServerEndpoints = ["http://localhost:5000"]
                }
            ]
        };
    }

    private static void AddMockInfrastructure(IServiceCollection services)
    {
        var mockMultiplexer = new Mock<IConnectionMultiplexer>();
        var mockDatabase = new Mock<IDatabase>();
        var mockServer = new Mock<IServer>();
        var endpoint = new DnsEndPoint("localhost", 6379);

        mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(mockDatabase.Object);
        mockMultiplexer.Setup(m => m.GetEndPoints(It.IsAny<bool>()))
            .Returns([endpoint]);
        mockMultiplexer.Setup(m => m.GetServer(endpoint, It.IsAny<object>()))
            .Returns(mockServer.Object);

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
    public void ConfigureAgents_RegistersCoreServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<IHostApplicationLifetime>().Object);
        var settings = CreateTestSettings();
        var cmdParams = new CommandLineParams();

        // Act
        var config = new ConfigurationBuilder().Build();
        services.ConfigureAgents(settings, cmdParams, config);
        AddMockInfrastructure(services);
        var provider = services.BuildServiceProvider();

        // Assert
        provider.GetService<IAgentFactory>().ShouldNotBeNull();
        provider.GetService<ChatMonitor>().ShouldNotBeNull();
        provider.GetService<ChatThreadResolver>().ShouldNotBeNull();
    }

    [Fact]
    public void GetCommandLineParams_WithChatOption_ParsesCorrectly()
    {
        // Act
        var result = ConfigModule.GetCommandLineParams(["--chat", "Cli"]);

        // Assert
        result.ChatInterface.ShouldBe(ChatInterface.Cli);
    }

    [Theory]
    [InlineData("--prompt", "Hello world")]
    [InlineData("-p", "Test prompt")]
    public void GetCommandLineParams_WithPromptFlag_SetsOneShotMode(string flag, string prompt)
    {
        var result = ConfigModule.GetCommandLineParams([flag, prompt]);

        result.ChatInterface.ShouldBe(ChatInterface.OneShot);
        result.Prompt.ShouldBe(prompt);
    }

    [Theory]
    [InlineData("--prompt", "--reasoning")]
    [InlineData("-p", "-r")]
    public void GetCommandLineParams_WithReasoningFlag_SetsShowReasoning(string promptFlag, string reasoningFlag)
    {
        var result = ConfigModule.GetCommandLineParams([promptFlag, "Test", reasoningFlag]);

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
    public void GetCommandLineParams_WithNoOptions_DefaultsToWeb()
    {
        // Act
        var result = ConfigModule.GetCommandLineParams([]);

        // Assert
        result.ChatInterface.ShouldBe(ChatInterface.Web);
        result.Prompt.ShouldBeNull();
        result.ShowReasoning.ShouldBeFalse();
    }
}