using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.LLMAdapters.OpenRouter;

public class OpenRouterAdapter(HttpClient client, string model) : ILargeLanguageModel
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)
        }
    };

    public async Task<AgentResponse[]> Prompt(
        IEnumerable<Message> messages,
        IEnumerable<ToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        var request = new OpenRouterRequest
        {
            Model = model,
            Messages = messages.Select(m => m.ToOpenRouterMessage()).ToArray(),
            Tools = tools.Select(t => t.ToOpenRouterTool()).ToArray()
        };

        const int maxRetryAttempts = 3;
        const int baseDelayMs = 1000;
        OpenRouterResponse? openRouterResponse = null;
        for (var attempt = 1; attempt <= maxRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            openRouterResponse = await SendPrompt(request, cancellationToken);
            if (IsResponseSuccessful(openRouterResponse))
            {
                break;
            }

            if (attempt == maxRetryAttempts)
            {
                throw new Exception($"OpenRouter failed after {maxRetryAttempts} attempts.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(baseDelayMs * Math.Pow(2, attempt)), cancellationToken);
        }

        return openRouterResponse?.ToAgentResponses() ?? [];
    }

    private async Task<OpenRouterResponse?> SendPrompt(OpenRouterRequest request, CancellationToken cancellationToken)
    {
        var response = await client
            .PostAsJsonAsync("chat/completions", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content
            .ReadFromJsonAsync<OpenRouterResponse>(_jsonOptions, cancellationToken);
    }

    private static bool IsResponseSuccessful(OpenRouterResponse? response)
    {
        var result = response?.Choices
            .All(x => x.FinishReason is not null && x.FinishReason != FinishReason.Error);
        return response is not null && result == true;
    }
}