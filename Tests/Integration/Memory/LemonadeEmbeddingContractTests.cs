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

        // The claim being tested is that Lemonade's loaded-model limit is per model type, so
        // a pinned embedding model cannot take the slot a speech model needs. Reading the
        // slots back is what makes that a test rather than a comment: a global cap would show
        // the speech types with no slot of their own, and a pinned model can never be evicted
        // to make room, so every utterance would fail or pay a full reload.
        var maxModels = health.RootElement.GetProperty("max_models");
        foreach (var speechType in new[] { "transcription", "tts" })
        {
            maxModels.TryGetProperty(speechType, out var slots).ShouldBeTrue(
                $"health should report a model slot for {speechType} separate from embedding");
            slots.GetInt32().ShouldBeGreaterThan(0, $"{speechType} must keep a slot of its own");
        }
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