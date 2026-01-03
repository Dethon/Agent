using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;
using JetBrains.Annotations;

namespace Infrastructure.Memory;

public class OpenRouterEmbeddingService(HttpClient httpClient, string model)
    : IEmbeddingService
{
    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var request = new EmbeddingRequest(model, text);
        var response = await httpClient.PostAsJsonAsync("embeddings", request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(ct);
        return result?.Data.FirstOrDefault()?.Embedding ?? throw new InvalidOperationException("No embedding returned");
    }

    public async Task<float[][]> GenerateEmbeddingsAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var textArray = texts.ToArray();
        if (textArray.Length == 0)
        {
            return [];
        }

        var request = new EmbeddingBatchRequest(model, textArray);
        var response = await httpClient.PostAsJsonAsync("embeddings", request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(ct);
        return result?.Data
            .OrderBy(d => d.Index)
            .Select(d => d.Embedding)
            .ToArray() ?? [];
    }
}

[UsedImplicitly]
internal record EmbeddingRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] string Input);

[UsedImplicitly]
internal record EmbeddingBatchRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] string[] Input);

internal record EmbeddingResponse(
    [property: JsonPropertyName("data")] EmbeddingData[] Data,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("usage")] EmbeddingUsage? Usage);

internal record EmbeddingData(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("embedding")]
    float[] Embedding);

[UsedImplicitly]
internal record EmbeddingUsage(
    [property: JsonPropertyName("prompt_tokens")]
    int PromptTokens,
    [property: JsonPropertyName("total_tokens")]
    int TotalTokens);