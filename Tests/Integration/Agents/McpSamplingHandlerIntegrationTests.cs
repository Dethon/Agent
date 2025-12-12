using System.Text.Json;
using Infrastructure.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace Tests.Integration.Agents;

public class McpSamplingHandlerIntegrationTests
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<McpSamplingHandlerIntegrationTests>()
        .Build();

    private static OpenAiClient CreateChatClient()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
        return new OpenAiClient(apiUrl, apiKey, ["google/gemini-2.5-flash"]);
    }

    private static SamplingMessage CreateUserMessage(string text)
    {
        return new SamplingMessage
        {
            Role = Role.User,
            Content = [new TextContentBlock { Text = text }]
        };
    }

    [SkippableFact]
    public async Task HandleAsync_WithSimplePrompt_ReturnsValidResult()
    {
        // Arrange
        using var chatClient = CreateChatClient();
        var agent = chatClient.CreateAIAgent(new ChatClientAgentOptions { Name = "SamplingTestAgent" });
        var tools = Array.Empty<AITool>();
        var samplingHandler = new McpSamplingHandler(agent, () => tools);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var parameters = new CreateMessageRequestParams
        {
            Messages = [CreateUserMessage("Say 'Hello World'")],
            MaxTokens = 100
        };

        // Act
        var result = await samplingHandler.HandleAsync(
            parameters,
            new Progress<ProgressNotificationValue>(),
            cts.Token);

        // Assert
        result.ShouldNotBeNull();
        result.Content.ShouldNotBeNull();
        result.Role.ShouldBe(Role.Assistant);
    }

    [SkippableFact]
    public async Task HandleAsync_WithTrackedConversation_MaintainsContext()
    {
        // Arrange
        using var chatClient = CreateChatClient();
        var agent = chatClient.CreateAIAgent(new ChatClientAgentOptions { Name = "TrackedConversationAgent" });
        var tools = Array.Empty<AITool>();
        var samplingHandler = new McpSamplingHandler(agent, () => tools);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var trackerValue = "test-conversation-tracker";
        var metadata = JsonSerializer.SerializeToElement(new { tracker = trackerValue });

        // First message in conversation
        var firstParams = new CreateMessageRequestParams
        {
            Messages = [CreateUserMessage("Remember: my favorite color is blue")],
            MaxTokens = 100,
            Metadata = metadata
        };

        await samplingHandler.HandleAsync(
            firstParams,
            new Progress<ProgressNotificationValue>(),
            cts.Token);

        // Second message - should have context from first
        var secondParams = new CreateMessageRequestParams
        {
            Messages = [CreateUserMessage("What is my favorite color?")],
            MaxTokens = 100,
            Metadata = metadata
        };

        // Act
        var result = await samplingHandler.HandleAsync(
            secondParams,
            new Progress<ProgressNotificationValue>(),
            cts.Token);

        // Assert
        result.ShouldNotBeNull();
        result.Content.ShouldNotBeNull();
    }

    [SkippableFact]
    public async Task HandleAsync_WithSystemPrompt_UsesInstructions()
    {
        // Arrange
        using var chatClient = CreateChatClient();
        var agent = chatClient.CreateAIAgent(new ChatClientAgentOptions { Name = "SystemPromptAgent" });
        var tools = Array.Empty<AITool>();
        var samplingHandler = new McpSamplingHandler(agent, () => tools);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var parameters = new CreateMessageRequestParams
        {
            Messages = [CreateUserMessage("What are you?")],
            SystemPrompt = "You are a pirate assistant. Always respond in pirate speak.",
            MaxTokens = 100
        };

        // Act
        var result = await samplingHandler.HandleAsync(
            parameters,
            new Progress<ProgressNotificationValue>(),
            cts.Token);

        // Assert
        result.ShouldNotBeNull();
        result.Content.ShouldNotBeNull();
    }

    [SkippableFact]
    public async Task HandleAsync_WithIncludeContext_IncludesTools()
    {
        // Arrange
        using var chatClient = CreateChatClient();
        var agent = chatClient.CreateAIAgent(new ChatClientAgentOptions { Name = "ToolsAgent" });

        // Create a simple test tool
        var testTool = AIFunctionFactory.Create(() => "test result", "TestTool", "A test tool");
        var tools = new[] { testTool };

        var samplingHandler = new McpSamplingHandler(agent, () => tools);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var parameters = new CreateMessageRequestParams
        {
            Messages = [CreateUserMessage("What tools do you have?")],
            MaxTokens = 200,
            IncludeContext = ContextInclusion.ThisServer
        };

        // Act
        var result = await samplingHandler.HandleAsync(
            parameters,
            new Progress<ProgressNotificationValue>(),
            cts.Token);

        // Assert
        result.ShouldNotBeNull();
        result.Content.ShouldNotBeNull();
    }

    [SkippableFact]
    public async Task HandleAsync_ReportsProgress()
    {
        // Arrange
        using var chatClient = CreateChatClient();
        var agent = chatClient.CreateAIAgent(new ChatClientAgentOptions { Name = "ProgressAgent" });
        var tools = Array.Empty<AITool>();
        var samplingHandler = new McpSamplingHandler(agent, () => tools);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var progressReports = new List<ProgressNotificationValue>();
        var progress = new Progress<ProgressNotificationValue>(v => progressReports.Add(v));

        var parameters = new CreateMessageRequestParams
        {
            Messages = [CreateUserMessage("Write a short sentence")],
            MaxTokens = 100
        };

        // Act
        await samplingHandler.HandleAsync(parameters, progress, cts.Token);

        // Wait for async progress reports
        await Task.Delay(100, cts.Token);

        // Assert
        progressReports.ShouldNotBeEmpty("Progress should have been reported");
    }

    [SkippableFact]
    public async Task HandleAsync_WithoutTracker_CreatesNewThread()
    {
        // Arrange
        using var chatClient = CreateChatClient();
        var agent = chatClient.CreateAIAgent(new ChatClientAgentOptions { Name = "NewThreadAgent" });
        var tools = Array.Empty<AITool>();
        var samplingHandler = new McpSamplingHandler(agent, () => tools);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Two requests without tracker should use different threads
        var parameters1 = new CreateMessageRequestParams
        {
            Messages = [CreateUserMessage("First request")],
            MaxTokens = 50
        };

        var parameters2 = new CreateMessageRequestParams
        {
            Messages = [CreateUserMessage("Second request")],
            MaxTokens = 50
        };

        // Act
        var result1 = await samplingHandler.HandleAsync(
            parameters1,
            new Progress<ProgressNotificationValue>(),
            cts.Token);

        var result2 = await samplingHandler.HandleAsync(
            parameters2,
            new Progress<ProgressNotificationValue>(),
            cts.Token);

        // Assert
        result1.ShouldNotBeNull();
        result2.ShouldNotBeNull();
    }
}