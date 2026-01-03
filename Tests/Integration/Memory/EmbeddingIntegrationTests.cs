using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Memory;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Memory;

public class OpenRouterEmbeddingServiceIntegrationTests : IAsyncLifetime
{
    private readonly string? _apiKey;
    private readonly string? _apiUrl;

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500)); // Rate limiting courtesy
    }

    public OpenRouterEmbeddingServiceIntegrationTests()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<OpenRouterEmbeddingServiceIntegrationTests>()
            .AddEnvironmentVariables()
            .Build();

        _apiKey = config["openRouter:apiKey"];
        _apiUrl = config["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
    }

    private bool HasApiKey => !string.IsNullOrEmpty(_apiKey);

    private OpenRouterEmbeddingService CreateService()
    {
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(_apiUrl!),
            DefaultRequestHeaders = { { "Authorization", $"Bearer {_apiKey}" } }
        };

        return new OpenRouterEmbeddingService(httpClient, "openai/text-embedding-3-small");
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var magnitude = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return magnitude == 0 ? 0 : dot / magnitude;
    }

    [SkippableFact]
    public async Task GenerateEmbeddingAsync_WithRealApi_ReturnsEmbedding()
    {
        Skip.IfNot(HasApiKey, "OpenRouter API key not configured");

        // Arrange
        var service = CreateService();

        // Act
        var embedding = await service.GenerateEmbeddingAsync("User prefers concise responses");

        // Assert
        embedding.ShouldNotBeNull();
        embedding.Length.ShouldBeGreaterThan(0);
        embedding.Length.ShouldBe(1536); // text-embedding-3-small dimension
    }

    [SkippableFact]
    public async Task GenerateEmbeddingsAsync_WithMultipleTexts_ReturnsBatchEmbeddings()
    {
        Skip.IfNot(HasApiKey, "OpenRouter API key not configured");

        // Arrange
        var service = CreateService();
        var texts = new[]
        {
            "User is a Python developer",
            "User prefers detailed explanations",
            "User works on machine learning projects"
        };

        // Act
        var embeddings = await service.GenerateEmbeddingsAsync(texts);

        // Assert
        embeddings.Length.ShouldBe(3);
        embeddings.ShouldAllBe(e => e.Length == 1536);
    }

    [SkippableFact]
    public async Task GenerateEmbeddingAsync_SimilarTexts_HaveHighCosineSimilarity()
    {
        Skip.IfNot(HasApiKey, "OpenRouter API key not configured");

        // Arrange
        var service = CreateService();

        // Act
        var embedding1 = await service.GenerateEmbeddingAsync("User likes Python programming");
        var embedding2 = await service.GenerateEmbeddingAsync("User enjoys coding in Python");
        var embedding3 = await service.GenerateEmbeddingAsync("User prefers cooking Italian food");

        var similaritySimilar = CosineSimilarity(embedding1, embedding2);
        var similarityDifferent = CosineSimilarity(embedding1, embedding3);

        // Assert - Similar texts should have higher similarity than different topics
        similaritySimilar.ShouldBeGreaterThan(similarityDifferent);
        similaritySimilar.ShouldBeGreaterThan(0.75f); // Similar texts should be quite similar
        similarityDifferent.ShouldBeLessThan(0.7f); // Different topics should be less similar
    }

    [SkippableFact]
    public async Task GenerateEmbeddingAsync_SemanticSearch_FindsRelevantContent()
    {
        Skip.IfNot(HasApiKey, "OpenRouter API key not configured");

        // Arrange
        var service = CreateService();
        var memories = new[]
        {
            "User is an expert in Kubernetes and container orchestration",
            "User prefers dark mode in all applications",
            "User works at a fintech startup",
            "User is learning Rust programming language"
        };

        var memoryEmbeddings = await service.GenerateEmbeddingsAsync(memories);
        var queryEmbedding = await service.GenerateEmbeddingAsync("What does the user know about containers?");

        // Act - Find most similar memory
        var similarities = memoryEmbeddings
            .Select((e, i) => (Index: i, Similarity: CosineSimilarity(queryEmbedding, e)))
            .OrderByDescending(x => x.Similarity)
            .ToList();

        // Assert - Kubernetes/container memory should be most relevant
        similarities[0].Index.ShouldBe(0); // "Kubernetes and container orchestration"
    }
}

public class MemoryStoreWithEmbeddingsIntegrationTests : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private readonly RedisFixture _redisFixture;
    private readonly string? _apiKey;
    private readonly string? _apiUrl;

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500));
    }

    public MemoryStoreWithEmbeddingsIntegrationTests(RedisFixture redisFixture)
    {
        _redisFixture = redisFixture;

        var config = new ConfigurationBuilder()
            .AddUserSecrets<MemoryStoreWithEmbeddingsIntegrationTests>()
            .AddEnvironmentVariables()
            .Build();

        _apiKey = config["openRouter:apiKey"];
        _apiUrl = config["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
    }

    private bool HasApiKey => !string.IsNullOrEmpty(_apiKey);

    private IEmbeddingService CreateEmbeddingService()
    {
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(_apiUrl!),
            DefaultRequestHeaders = { { "Authorization", $"Bearer {_apiKey}" } }
        };
        return new OpenRouterEmbeddingService(httpClient, "openai/text-embedding-3-small");
    }

    private RedisStackMemoryStore CreateStore()
    {
        return new RedisStackMemoryStore(_redisFixture.Connection);
    }

    private async Task<MemoryEntry> CreateMemoryWithEmbedding(
        IEmbeddingService embeddingService,
        string userId,
        string content,
        MemoryCategory category = MemoryCategory.Fact)
    {
        var embedding = await embeddingService.GenerateEmbeddingAsync(content);
        return new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Tier = MemoryTier.LongTerm,
            Category = category,
            Content = content,
            Importance = 0.7,
            Confidence = 0.8,
            Embedding = embedding,
            Tags = [],
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        };
    }

    [SkippableFact]
    public async Task FullFlow_StoreAndRetrieveWithSemanticSearch()
    {
        Skip.IfNot(HasApiKey, "OpenRouter API key not configured");

        // Arrange
        var store = CreateStore();
        var embeddingService = CreateEmbeddingService();
        var userId = $"user_{Guid.NewGuid():N}";

        // Store memories with embeddings
        var memory1 = await CreateMemoryWithEmbedding(embeddingService, userId,
            "User is a senior backend developer specializing in Go and microservices");
        var memory2 = await CreateMemoryWithEmbedding(embeddingService, userId,
            "User prefers vim keybindings in all editors");
        var memory3 = await CreateMemoryWithEmbedding(embeddingService, userId,
            "User is working on a distributed cache system");
        var memory4 = await CreateMemoryWithEmbedding(embeddingService, userId,
            "User likes coffee, especially dark roast");

        await store.StoreAsync(memory1);
        await store.StoreAsync(memory2);
        await store.StoreAsync(memory3);
        await store.StoreAsync(memory4);

        // Act - Search for programming-related memories
        var queryEmbedding = await embeddingService.GenerateEmbeddingAsync(
            "What programming languages and systems does the user work with?");

        var results = await store.SearchAsync(userId, queryEmbedding: queryEmbedding, limit: 2);

        // Assert
        results.Count.ShouldBe(2);

        // Programming-related memories should rank higher than coffee preference
        var contents = results.Select(r => r.Memory.Content).ToList();
        contents.ShouldNotContain(c => c.Contains("coffee"));

        // At least one of the top results should be about programming/systems
        contents.Any(c => c.Contains("backend") || c.Contains("microservices") || c.Contains("distributed"))
            .ShouldBeTrue();
    }

    [SkippableFact]
    public async Task FullFlow_CategoryFilterWithSemanticRanking()
    {
        Skip.IfNot(HasApiKey, "OpenRouter API key not configured");

        // Arrange
        var store = CreateStore();
        var embeddingService = CreateEmbeddingService();
        var userId = $"user_{Guid.NewGuid():N}";

        // Store mixed category memories
        var pref1 = await CreateMemoryWithEmbedding(embeddingService, userId,
            "User prefers TypeScript over JavaScript", MemoryCategory.Preference);
        var pref2 = await CreateMemoryWithEmbedding(embeddingService, userId,
            "User likes concise code with minimal comments", MemoryCategory.Preference);
        var skill1 = await CreateMemoryWithEmbedding(embeddingService, userId,
            "User is proficient in TypeScript and React", MemoryCategory.Skill);

        await store.StoreAsync(pref1);
        await store.StoreAsync(pref2);
        await store.StoreAsync(skill1);

        // Act - Search preferences only for TypeScript-related query
        var queryEmbedding = await embeddingService.GenerateEmbeddingAsync("TypeScript coding style");

        var results = await store.SearchAsync(
            userId,
            queryEmbedding: queryEmbedding,
            categories: [MemoryCategory.Preference],
            limit: 5);

        // Assert
        results.ShouldNotBeEmpty();
        results.ShouldAllBe(r => r.Memory.Category == MemoryCategory.Preference);

        // TypeScript preference should rank higher due to semantic similarity
        results[0].Memory.Content.ShouldContain("TypeScript");
    }

    [SkippableFact]
    public async Task FullFlow_UserIsolation_SemanticSearchRespectsBoundaries()
    {
        Skip.IfNot(HasApiKey, "OpenRouter API key not configured");

        // Arrange
        var store = CreateStore();
        var embeddingService = CreateEmbeddingService();
        var user1 = $"user_{Guid.NewGuid():N}";
        var user2 = $"user_{Guid.NewGuid():N}";

        // Both users have Python-related memories
        var user1Memory = await CreateMemoryWithEmbedding(embeddingService, user1,
            "User 1 is a Python expert with 10 years experience");
        var user2Memory = await CreateMemoryWithEmbedding(embeddingService, user2,
            "User 2 is learning Python as their first language");

        await store.StoreAsync(user1Memory);
        await store.StoreAsync(user2Memory);

        // Act - Search for Python in user1's context
        var queryEmbedding = await embeddingService.GenerateEmbeddingAsync("Python programming experience");
        var user1Results = await store.SearchAsync(user1, queryEmbedding: queryEmbedding);
        var user2Results = await store.SearchAsync(user2, queryEmbedding: queryEmbedding);

        // Assert - Each user only sees their own memories
        user1Results.Count.ShouldBe(1);
        user1Results[0].Memory.Content.ShouldContain("User 1");

        user2Results.Count.ShouldBe(1);
        user2Results[0].Memory.Content.ShouldContain("User 2");
    }

    [SkippableFact]
    public async Task FullFlow_ImportanceWeighting_AffectsRanking()
    {
        Skip.IfNot(HasApiKey, "OpenRouter API key not configured");

        // Arrange
        var store = CreateStore();
        var embeddingService = CreateEmbeddingService();
        var userId = $"user_{Guid.NewGuid():N}";

        // Create two similar memories with different importance
        var embedding = await embeddingService.GenerateEmbeddingAsync("User works with databases");

        var lowImportance = new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Tier = MemoryTier.LongTerm,
            Category = MemoryCategory.Skill,
            Content = "User mentioned databases once",
            Importance = 0.2,
            Confidence = 0.5,
            Embedding = embedding,
            Tags = [],
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        };

        var highImportance = new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Tier = MemoryTier.LongTerm,
            Category = MemoryCategory.Skill,
            Content = "User explicitly stated expertise in PostgreSQL databases",
            Importance = 0.95,
            Confidence = 0.9,
            Embedding = embedding, // Same embedding for fair comparison
            Tags = [],
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        };

        await store.StoreAsync(lowImportance);
        await store.StoreAsync(highImportance);

        // Act
        var queryEmbedding = await embeddingService.GenerateEmbeddingAsync("database skills");
        var results = await store.SearchAsync(userId, queryEmbedding: queryEmbedding);

        // Assert - High importance should rank first due to weighted relevance
        results.Count.ShouldBe(2);
        results[0].Memory.Importance.ShouldBeGreaterThan(results[1].Memory.Importance);
        results[0].Memory.Content.ShouldContain("PostgreSQL");
    }
}