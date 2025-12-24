using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Integration.Agents;

public class OpenAiClientFallbackIntegrationTests
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<OpenAiClientFallbackIntegrationTests>()
        .Build();

    private static (string apiUrl, string apiKey) GetOpenRouterConfig()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
        return (apiUrl, apiKey);
    }

    private static IChatClient CreateRealClient(string apiUrl, string apiKey, string model)
    {
        return new OpenAiClient(apiUrl, apiKey, [model], useFunctionInvocation: false);
    }

    [Fact]
    public async Task GetResponseAsync_WhenPrimaryFails_FallsBackToRealClient()
    {
        // Arrange - fake primary that throws, real fallback
        var (apiUrl, apiKey) = GetOpenRouterConfig();

        var failingPrimary = new FailingChatClient();
        var realFallback = CreateRealClient(apiUrl, apiKey, "google/gemini-2.0-flash-001");

        using var client = new OpenAiClient(failingPrimary, [realFallback]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Say 'fallback worked' and nothing else.")],
            cancellationToken: cts.Token);

        // Assert
        var allText = string.Join("", response.Messages.Select(m => m.Text));
        allText.ShouldContain("Switching to");
        allText.ShouldContain("google/gemini-2.0-flash-001");
        allText.ToLower().ShouldContain("fallback");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WhenPrimaryFails_FallsBackToRealClient()
    {
        // Arrange - fake primary that throws, real fallback
        var (apiUrl, apiKey) = GetOpenRouterConfig();

        var failingPrimary = new FailingChatClient();
        var realFallback = CreateRealClient(apiUrl, apiKey, "google/gemini-2.0-flash-001");

        using var client = new OpenAiClient(failingPrimary, [realFallback]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "Say 'fallback worked' and nothing else.")],
                           ct: cts.Token))
        {
            updates.Add(update);
        }

        // Assert
        var allText = string.Join("", updates.Select(u => u.Text));
        allText.ShouldContain("Switching to");
        allText.ShouldContain("google/gemini-2.0-flash-001");
        allText.ToLower().ShouldContain("fallback");
    }

    [Fact]
    public async Task GetResponseAsync_WhenContentFiltered_FallsBackToRealClient()
    {
        // Arrange - fake primary that returns content filter, real fallback
        var (apiUrl, apiKey) = GetOpenRouterConfig();

        var filteringPrimary = new ContentFilteringChatClient();
        var realFallback = CreateRealClient(apiUrl, apiKey, "google/gemini-2.0-flash-001");

        using var client = new OpenAiClient(filteringPrimary, [realFallback]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Say 'content filter fallback' and nothing else.")],
            cancellationToken: cts.Token);

        // Assert
        var allText = string.Join("", response.Messages.Select(m => m.Text));
        allText.ShouldContain("Switching to");
        allText.ShouldContain("content filter");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WhenContentFiltered_FallsBackToRealClient()
    {
        // Arrange - fake primary that returns content filter, real fallback
        var (apiUrl, apiKey) = GetOpenRouterConfig();

        var filteringPrimary = new ContentFilteringChatClient();
        var realFallback = CreateRealClient(apiUrl, apiKey, "google/gemini-2.0-flash-001");

        using var client = new OpenAiClient(filteringPrimary, [realFallback]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "Say 'content filter fallback' and nothing else.")],
                           ct: cts.Token))
        {
            updates.Add(update);
        }

        // Assert
        var allText = string.Join("", updates.Select(u => u.Text));
        allText.ShouldContain("Switching to");
        allText.ShouldContain("content filter");
    }

    [Fact]
    public async Task GetResponseAsync_WithSingleModel_ReturnsResponse()
    {
        // Arrange
        var (apiUrl, apiKey) = GetOpenRouterConfig();
        var models = new[] { "google/gemini-2.0-flash-001" };
        using var client = new OpenAiClient(apiUrl, apiKey, models, useFunctionInvocation: false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Say 'hello' and nothing else.")],
            cancellationToken: cts.Token);

        // Assert
        response.Messages.ShouldNotBeEmpty();
        response.Messages.Last().Text.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithSingleModel_StreamsResponse()
    {
        // Arrange
        var (apiUrl, apiKey) = GetOpenRouterConfig();
        var models = new[] { "google/gemini-2.0-flash-001" };
        using var client = new OpenAiClient(apiUrl, apiKey, models, useFunctionInvocation: false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "Say 'hello' and nothing else.")],
                           ct: cts.Token))
        {
            updates.Add(update);
        }

        // Assert
        updates.ShouldNotBeEmpty();
        var allText = string.Join("", updates.Select(u => u.Text));
        allText.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetResponseAsync_WithFunctionInvocation_ExecutesTools()
    {
        // Arrange
        var (apiUrl, apiKey) = GetOpenRouterConfig();
        var models = new[] { "google/gemini-2.0-flash-001" };
        using var client = new OpenAiClient(apiUrl, apiKey, models, useFunctionInvocation: true);

        var toolInvoked = false;
        var tool = AIFunctionFactory.Create(() =>
        {
            toolInvoked = true;
            return "Tool result: success";
        }, "TestTool", "A test tool that returns success");

        var options = new ChatOptions { Tools = [tool] };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Call the TestTool function.")],
            options,
            cts.Token);

        // Assert
        response.Messages.ShouldNotBeEmpty();
        toolInvoked.ShouldBeTrue("Tool should have been invoked");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithFunctionInvocation_ExecutesTools()
    {
        // Arrange
        var (apiUrl, apiKey) = GetOpenRouterConfig();
        var models = new[] { "google/gemini-2.0-flash-001" };
        using var client = new OpenAiClient(apiUrl, apiKey, models, useFunctionInvocation: true);

        var toolInvoked = false;
        var tool = AIFunctionFactory.Create(() =>
        {
            toolInvoked = true;
            return "Tool result: success";
        }, "TestTool", "A test tool that returns success");

        var options = new ChatOptions { Tools = [tool] };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "Call the TestTool function.")],
                           options,
                           cts.Token))
        {
            updates.Add(update);
        }

        // Assert
        updates.ShouldNotBeEmpty();
        toolInvoked.ShouldBeTrue("Tool should have been invoked");
    }

    /// <summary>
    ///     Fake client that throws ArgumentOutOfRangeException simulating unknown ChatFinishReason.
    /// </summary>
    [SuppressMessage("ReSharper", "NotResolvedInText")]
    private sealed class FailingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Use streaming");
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new ArgumentOutOfRangeException("value", "Unknown ChatFinishReason value.");
#pragma warning disable CS0162 // Unreachable code detected
            // ReSharper disable once HeuristicUnreachableCode
            yield break;
#pragma warning restore CS0162 // Unreachable code detected
        }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }
    }

    /// <summary>
    ///     Fake client that returns a content-filtered response.
    /// </summary>
    private sealed class ContentFilteringChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Use streaming");
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent("Content was filtered.")]
            };
            yield return new ChatResponseUpdate
            {
                FinishReason = ChatFinishReason.ContentFilter
            };
            await Task.CompletedTask;
        }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }
    }
}