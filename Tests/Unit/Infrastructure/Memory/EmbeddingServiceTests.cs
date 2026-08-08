using System.Net;
using System.Text.Json;
using Infrastructure.Memory;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Unit.Infrastructure.Memory;

public class EmbeddingServiceTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly HttpClient _client;
    private readonly EmbeddingService _service;

    public EmbeddingServiceTests()
    {
        _server = WireMockServer.Start();
        _client = new HttpClient();
        _service = new EmbeddingService(_client, new EmbeddingOptions
        {
            BaseAddress = _server.Url!,
            Model = "test-model"
        });
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_WithValidText_ReturnsEmbeddingAndSendsCorrectRequest()
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

        // Assert - response
        result.ShouldNotBeNull();
        result.Length.ShouldBe(3);
        result[0].ShouldBe(0.1f);
        result[1].ShouldBe(0.2f);
        result[2].ShouldBe(0.3f);

        // Assert - request
        var request = _server.LogEntries.First();
        var body = request.RequestMessage?.Body!;
        body.ShouldContain("\"model\":\"test-model\"");
        body.ShouldContain("\"input\":\"test text\"");
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
        result[0].ShouldBe([0.1f, 0.2f]);
        result[1].ShouldBe([0.4f, 0.5f]);
        result[2].ShouldBe([0.7f, 0.8f]);
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

        // Assert
        _server.LogEntries.Count.ShouldBe(1);
        var body = _server.LogEntries[0].RequestMessage?.Body!;
        body.ShouldContain("\"input\":[\"text1\",\"text2\"]");
    }

    [Fact]
    public async Task WithNoKeyConfigured_SendsNoAuthorizationHeader()
    {
        StubEmbeddingResponse();

        await _service.GenerateEmbeddingAsync("test");

        var headers = _server.LogEntries[0].RequestMessage?.Headers!;
        headers.ShouldNotContainKey("Authorization");
    }

    [Fact]
    public async Task WithAKeyConfigured_SendsItAsABearerToken()
    {
        StubEmbeddingResponse();
        using var client = new HttpClient();
        var service = new EmbeddingService(client, new EmbeddingOptions
        {
            BaseAddress = _server.Url!,
            Model = "test-model",
            ApiKey = "sk-test"
        });

        await service.GenerateEmbeddingAsync("test");

        var headers = _server.LogEntries[0].RequestMessage?.Headers!;
        headers["Authorization"].ShouldContain("Bearer sk-test");
    }

    private void StubEmbeddingResponse()
    {
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
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }
}