using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure;

[SuppressMessage("ReSharper", "NotResolvedInText")]
public class OpenAiClientFallbackTests
{
    [Fact]
    public async Task GetResponseAsync_WhenContentFiltered_FallsBackToNextClient()
    {
        // Arrange
        var primaryClient = new FakeChatClient("primary-model");
        primaryClient.SetStreamingResponse(CreateContentFilteredStreamingResponse());

        var fallbackClient = new FakeChatClient("fallback-model");
        fallbackClient.SetStreamingResponse(CreateSuccessStreamingResponse("Fallback response"));

        var client = new OpenAiClient(primaryClient, [fallbackClient]);

        // Act
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);

        // Assert
        response.Messages.Last().Text.ShouldContain("Fallback response");
        primaryClient.StreamingCallCount.ShouldBe(1);
        fallbackClient.StreamingCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetResponseAsync_WhenContentFiltered_IncludesFallbackMessage()
    {
        // Arrange
        var primaryClient = new FakeChatClient("primary-model");
        primaryClient.SetStreamingResponse(CreateContentFilteredStreamingResponse());

        var fallbackClient = new FakeChatClient("fallback-model");
        fallbackClient.SetStreamingResponse(CreateSuccessStreamingResponse("Fallback response"));

        var client = new OpenAiClient(primaryClient, [fallbackClient]);

        // Act
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);

        // Assert - fallback message is included in the response
        var allText = string.Join("", response.Messages.Select(m => m.Text));
        allText.ShouldContain("Switching to fallback-model due to content filter");
    }

    [Fact]
    public async Task GetResponseAsync_WhenUnknownFinishReason_FallsBackToNextClient()
    {
        // Arrange
        var primaryClient = new FakeChatClient("primary-model");
        primaryClient.ThrowOnStreamingEnumeration(new ArgumentOutOfRangeException("value",
            "Unknown ChatFinishReason value."));

        var fallbackClient = new FakeChatClient("fallback-model");
        fallbackClient.SetStreamingResponse(CreateSuccessStreamingResponse("Fallback response"));

        var client = new OpenAiClient(primaryClient, [fallbackClient]);

        // Act
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);

        // Assert
        response.Messages.Last().Text.ShouldContain("Fallback response");
        fallbackClient.StreamingCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetResponseAsync_WhenUnknownFinishReason_IncludesFallbackMessage()
    {
        // Arrange
        var primaryClient = new FakeChatClient("primary-model");
        primaryClient.ThrowOnStreamingEnumeration(new ArgumentOutOfRangeException("value",
            "Unknown ChatFinishReason value."));

        var fallbackClient = new FakeChatClient("fallback-model");
        fallbackClient.SetStreamingResponse(CreateSuccessStreamingResponse("Fallback response"));

        var client = new OpenAiClient(primaryClient, [fallbackClient]);

        // Act
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);

        // Assert - fallback message is included in the response
        var allText = string.Join("", response.Messages.Select(m => m.Text));
        allText.ShouldContain("Switching to fallback-model due to unknown error");
    }

    [Fact]
    public async Task GetResponseAsync_WhenNoFallbackAvailable_ReturnsEmptyOnUnknownFinishReason()
    {
        // Arrange
        var primaryClient = new FakeChatClient("primary-model");
        primaryClient.ThrowOnStreamingEnumeration(new ArgumentOutOfRangeException("value",
            "Unknown ChatFinishReason value."));

        var client = new OpenAiClient(primaryClient, []);

        // Act
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")]);

        // Assert - no fallback available, returns empty response
        response.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WhenContentFiltered_FallsBackToNextClient()
    {
        // Arrange
        var primaryClient = new FakeChatClient("primary-model");
        primaryClient.SetStreamingResponse(CreateContentFilteredStreamingResponse());

        var fallbackClient = new FakeChatClient("fallback-model");
        fallbackClient.SetStreamingResponse(CreateSuccessStreamingResponse("Fallback response"));

        var client = new OpenAiClient(primaryClient, [fallbackClient]);

        // Act
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "test")]))
        {
            updates.Add(update);
        }

        // Assert
        var allText = string.Join("", updates.Select(u => u.Text));
        allText.ShouldContain("Fallback response");
        primaryClient.StreamingCallCount.ShouldBe(1);
        fallbackClient.StreamingCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WhenContentFiltered_IncludesFallbackMessage()
    {
        // Arrange
        var primaryClient = new FakeChatClient("primary-model");
        primaryClient.SetStreamingResponse(CreateContentFilteredStreamingResponse());

        var fallbackClient = new FakeChatClient("fallback-model");
        fallbackClient.SetStreamingResponse(CreateSuccessStreamingResponse("Fallback response"));

        var client = new OpenAiClient(primaryClient, [fallbackClient]);

        // Act
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "test")]))
        {
            updates.Add(update);
        }

        // Assert
        var allText = string.Join("", updates.Select(u => u.Text));
        allText.ShouldContain("Switching to fallback-model due to content filter");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WhenUnknownFinishReason_FallsBackToNextClient()
    {
        // Arrange
        var primaryClient = new FakeChatClient("primary-model");
        primaryClient.ThrowOnStreamingEnumeration(
            new ArgumentOutOfRangeException("value", "Unknown ChatFinishReason value."));

        var fallbackClient = new FakeChatClient("fallback-model");
        fallbackClient.SetStreamingResponse(CreateSuccessStreamingResponse("Fallback response"));

        var client = new OpenAiClient(primaryClient, [fallbackClient]);

        // Act
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "test")]))
        {
            updates.Add(update);
        }

        // Assert
        var allText = string.Join("", updates.Select(u => u.Text));
        allText.ShouldContain("Fallback response");
        fallbackClient.StreamingCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_StreamsImmediately()
    {
        // Arrange
        var primaryClient = new FakeChatClient("primary-model");
        primaryClient.SetStreamingResponse(CreateSuccessStreamingResponse("Hello world"));

        var client = new OpenAiClient(primaryClient, []);

        // Act
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "test")]))
        {
            updates.Add(update);
        }

        // Assert - should have received multiple streaming chunks
        updates.Count.ShouldBeGreaterThan(1);
    }

    private static ChatResponse CreateSuccessResponse(string text)
    {
        return new ChatResponse([new ChatMessage(ChatRole.Assistant, text)])
        {
            FinishReason = ChatFinishReason.Stop
        };
    }

    private static IReadOnlyList<ChatResponseUpdate> CreateContentFilteredStreamingResponse()
    {
        return
        [
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("Partial")] },
            new ChatResponseUpdate { FinishReason = ChatFinishReason.ContentFilter }
        ];
    }

    private static IReadOnlyList<ChatResponseUpdate> CreateSuccessStreamingResponse(string text)
    {
        var words = text.Split(' ');
        var updates = words.Select(w => new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent(w + " ")]
        }).ToList();
        updates.Add(new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop });
        return updates;
    }

    private sealed class FakeChatClient(string modelId) : IChatClient
    {
        private IReadOnlyList<ChatResponseUpdate>? _streamingResponse;
        private Exception? _throwOnCall;
        private Exception? _throwOnStreaming;

        private int CallCount { get; set; }
        public int StreamingCallCount { get; private set; }

        public void SetStreamingResponse(IReadOnlyList<ChatResponseUpdate> updates)
        {
            _streamingResponse = updates;
        }

        public void ThrowOnStreamingEnumeration(Exception ex)
        {
            _throwOnStreaming = ex;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (_throwOnCall is null)
            {
                return Task.FromResult(CreateSuccessResponse("Default response"));
            }

            var ex = _throwOnCall;
            _throwOnCall = null;
            throw ex;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingCallCount++;

            // Throw on first MoveNextAsync if configured
            if (_throwOnStreaming is not null)
            {
                var ex = _throwOnStreaming;
                _throwOnStreaming = null;
                throw ex;
            }

            if (_streamingResponse is null)
            {
                yield break;
            }

            foreach (var update in _streamingResponse)
            {
                yield return update;
                await Task.Yield();
            }
        }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(ChatClientMetadata)
                ? new ChatClientMetadata(defaultModelId: modelId)
                : null;
        }
    }
}