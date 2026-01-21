using System.Net;
using System.Text.Json;
using Infrastructure.Memory;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Unit.Infrastructure.Memory;

public class OpenRouterEmbeddingServiceMockTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly OpenRouterEmbeddingService _service;

    public OpenRouterEmbeddingServiceMockTests()
    {
        _server = WireMockServer.Start();
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(_server.Url!)
        };
        _service = new OpenRouterEmbeddingService(httpClient, "test-model");
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_WithValidText_ReturnsEmbedding()
    {
        // Arrange
        var response = new
        {
            data = new[]
            {
                new
                {
                    index = 0,
                    embedding = new[] { 0.1f, 0.2f, 0.3f }
                }
            },
            model = "test-model",
            usage = new { prompt_tokens = 5, total_tokens = 5 }
        };

        _server.Given(Request.Create()
                .WithPath("/embeddings")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(response)));

        // Act
        var result = await _service.GenerateEmbeddingAsync("test text");

        // Assert
        result.ShouldNotBeNull();
        result.Length.ShouldBe(3);
        result[0].ShouldBe(0.1f);
        result[1].ShouldBe(0.2f);
        result[2].ShouldBe(0.3f);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_SendsCorrectRequest()
    {
        // Arrange
        var response = new
        {
            data = new[] { new { index = 0, embedding = new[] { 0.1f } } },
            model = "test-model"
        };

        _server.Given(Request.Create()
                .WithPath("/embeddings")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(response)));

        // Act
        await _service.GenerateEmbeddingAsync("test input");

        // Assert
        var request = _server.LogEntries.First();
        var body = request.RequestMessage.Body!;
        body.ShouldContain("\"model\":\"test-model\"");
        body.ShouldContain("\"input\":\"test input\"");
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_WithMultipleTexts_ReturnsOrderedEmbeddings()
    {
        // Arrange
        var response = new
        {
            data = new[]
            {
                new { index = 1, embedding = new[] { 0.4f, 0.5f } }, // Out of order
                new { index = 0, embedding = new[] { 0.1f, 0.2f } },
                new { index = 2, embedding = new[] { 0.7f, 0.8f } }
            },
            model = "test-model"
        };

        _server.Given(Request.Create()
                .WithPath("/embeddings")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(response)));

        // Act
        var result = await _service.GenerateEmbeddingsAsync(["text1", "text2", "text3"]);

        // Assert
        result.Length.ShouldBe(3);
        result[0].ShouldBe([0.1f, 0.2f]); // Index 0
        result[1].ShouldBe([0.4f, 0.5f]); // Index 1
        result[2].ShouldBe([0.7f, 0.8f]); // Index 2
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_WithEmptyInput_ReturnsEmptyArray()
    {
        // Act
        var result = await _service.GenerateEmbeddingsAsync([]);

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_OnHttpError_ThrowsException()
    {
        // Arrange
        _server.Given(Request.Create()
                .WithPath("/embeddings")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.Unauthorized)
                .WithBody("Invalid API key"));

        // Act & Assert
        await Should.ThrowAsync<HttpRequestException>(() =>
            _service.GenerateEmbeddingAsync("test"));
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_WithEmptyResponse_ThrowsException()
    {
        // Arrange
        var response = new
        {
            data = Array.Empty<object>(),
            model = "test-model"
        };

        _server.Given(Request.Create()
                .WithPath("/embeddings")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(response)));

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() =>
            _service.GenerateEmbeddingAsync("test"));
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_SendsBatchRequest()
    {
        // Arrange
        var response = new
        {
            data = new[]
            {
                new { index = 0, embedding = new[] { 0.1f } },
                new { index = 1, embedding = new[] { 0.2f } }
            },
            model = "test-model"
        };

        _server.Given(Request.Create()
                .WithPath("/embeddings")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(response)));

        // Act
        await _service.GenerateEmbeddingsAsync(["text1", "text2"]);

        // Assert - Should be a single request with array input
        _server.LogEntries.Count.ShouldBe(1);
        var body = _server.LogEntries.First().RequestMessage.Body!;
        body.ShouldContain("\"input\":[\"text1\",\"text2\"]");
    }
}