using System.Net.Http.Json;
using System.Text.Json;
using Infrastructure.Memory;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Memory;

// Proves the real Lemonade server answers the request and response shape EmbeddingService
// sends, so a change in either side's wire format is caught here rather than in production.
// The fixture forces the CPU backend and needs no GPU passthrough, so this runs anywhere
// Docker does; it skips when the model cache is not provisioned.
[Trait("Category", "External")]
public class LemonadeEmbeddingContractTests(LemonadeFixture fixture) : IClassFixture<LemonadeFixture>
{
    [SkippableFact]
    public async Task TheRealServerAnswersTheShapeTheClientSends()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        var embedding = await CreateService().GenerateEmbeddingAsync("El usuario prefiere respuestas breves");

        embedding.Length.ShouldBe(LemonadeFixture.EmbeddingDimension);
        embedding.ShouldContain(value => value != 0f);
    }

    [SkippableFact]
    public async Task ABatchComesBackInTheOrderItWasSent()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        var service = CreateService();
        var single = await service.GenerateEmbeddingAsync("segundo");
        var batch = await service.GenerateEmbeddingsAsync(["primero", "segundo", "tercero"]);

        batch.Length.ShouldBe(3);
        batch.ShouldAllBe(e => e.Length == LemonadeFixture.EmbeddingDimension);
        batch[1].ShouldBe(single);
    }

    [SkippableFact]
    public async Task TheEmbeddingModelIsPinnedAndDisplacesNeitherSpeechModel()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);

        // The entrypoint pins it after start, in the background, so give it a moment rather
        // than racing the pull-and-load.
        var health = await ReadHealthUntilAsync(h =>
            PinnedCountOf(h, "embedding") > 0);

        PinnedCountOf(health, "embedding").ShouldBeGreaterThan(0);

        // Lemonade applies its loaded-model limit per model type, so pinning an embedding
        // model must not have cost a speech model its slot.
        var transcription = await CreateService().GenerateEmbeddingAsync("sigue vivo");
        transcription.Length.ShouldBe(LemonadeFixture.EmbeddingDimension);
        health.RootElement.TryGetProperty("max_models", out _).ShouldBeTrue(
            "health should report per-type model slots");
    }

    private EmbeddingService CreateService() => new(new HttpClient(), new EmbeddingOptions
    {
        BaseAddress = fixture.BaseUrl,
        Model = LemonadeFixture.EmbeddingModel,
        Dimension = LemonadeFixture.EmbeddingDimension,
        Timeout = TimeSpan.FromMinutes(2)
    });

    private static int PinnedCountOf(JsonDocument health, string modelType)
    {
        return health.RootElement.TryGetProperty("pinned_models", out var pinned)
               && pinned.TryGetProperty(modelType, out var count)
            ? count.GetInt32()
            : 0;
    }

    private async Task<JsonDocument> ReadHealthUntilAsync(Func<JsonDocument, bool> condition)
    {
        using var client = new HttpClient { BaseAddress = new Uri(HealthBaseAddress()) };
        JsonDocument? last = null;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await client.GetFromJsonAsync<JsonDocument>("api/v1/health");
            if (last is not null && condition(last))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return last ?? throw new InvalidOperationException("lemonade health never answered");
    }

    // BaseUrl carries the OpenAI-compatible /v1 suffix; the health route sits beside it.
    private string HealthBaseAddress() => fixture.BaseUrl[..^"v1".Length];
}