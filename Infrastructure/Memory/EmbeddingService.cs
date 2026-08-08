using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;
using JetBrains.Annotations;

namespace Infrastructure.Memory;

public record EmbeddingOptions
{
    // Qwen3-Embedding-0.6B, which is what the local Lemonade server is pre-pulled with.
    public const int DefaultDimension = 1024;

    public required string BaseAddress { get; init; }
    public required string Model { get; init; }
    public string? ApiKey { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    // The width of the vectors the model produces. The memory index is built at this width,
    // so changing it needs the stored vectors regenerated as well.
    public int Dimension { get; init; } = DefaultDimension;
}

// Plain OpenAI-compatible JSON, which is what both the hosted provider and the local
// Lemonade server speak, so which one it talks to is entirely a matter of configuration.
public class EmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    public EmbeddingService(HttpClient httpClient, EmbeddingOptions options)
    {
        _httpClient = httpClient;
        _model = options.Model;
        // A base address that does not end in a slash loses its last segment when a relative
        // path is resolved against it, so "http://host/v1" would post to "/embeddings".
        httpClient.BaseAddress = new Uri(options.BaseAddress.TrimEnd('/') + "/");
        httpClient.Timeout = options.Timeout;
        // A local server has no key to check, so an authorization header there would only put
        // the hosted provider's token on the wire for nothing.
        httpClient.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(options.ApiKey)
            ? null
            : new AuthenticationHeaderValue("Bearer", options.ApiKey);
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var request = new EmbeddingRequest(_model, text);
        var response = await _httpClient.PostAsJsonAsync("embeddings", request, ct);
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

        var request = new EmbeddingBatchRequest(_model, textArray);
        var response = await _httpClient.PostAsJsonAsync("embeddings", request, ct);
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