using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.LLMAdapters.OpenRouter;

public class OpenRouterAdapter(HttpClient client, string[] models) : ILargeLanguageModel
{
    private string _selectedModel = models.FirstOrDefault() ?? throw new ArgumentException("No model provided");

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
        bool enableSearch = false,
        float? temperature = null,
        CancellationToken cancellationToken = default)
    {
        var plugins = enableSearch ? new OpenRouterPlugin[] { new OpenRouterSearchPlugin() } : [];
        var mappedMessages = messages.Select(m => m.ToOpenRouterMessage()).ToArray();
        var mappedTools = tools.Select(t => t.ToOpenRouterTool()).ToArray();

        const int maxRetryAttempts = 5;
        const int baseDelayMs = 1000;
        for (var attempt = 0; attempt < maxRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new OpenRouterRequest
            {
                Model = _selectedModel,
                Plugins = plugins,
                Temperature = temperature,
                Messages = mappedMessages,
                Tools = mappedTools
            };
            var openRouterResponse = await SendPrompt(request, cancellationToken);
            if (IsResponseSuccessful(openRouterResponse))
            {
                return openRouterResponse?.ToAgentResponses() ?? [];
            }

            await Task.Delay(TimeSpan.FromMilliseconds(baseDelayMs * Math.Pow(2, attempt)), cancellationToken);
        }

        throw new HttpRequestException($"OpenRouter failed after {maxRetryAttempts} attempts.");
    }

    private async Task<OpenRouterResponse?> SendPrompt(OpenRouterRequest request, CancellationToken cancellationToken)
    {
        var response = await client.PostAsJsonAsync("chat/completions", request, _jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OpenRouterResponse>(_jsonOptions, cancellationToken);
    }

    private bool IsResponseSuccessful(OpenRouterResponse? response)
    {
        var hasErrors = response?.Choices.Any(x => x.FinishReason is null or FinishReason.Error) ?? true;
        var isCensored = response?.Choices.Any(x => x.FinishReason is FinishReason.ContentFilter) ?? false;

        if (!isCensored)
        {
            return !hasErrors;
        }

        var modelList = models.ToList();
        var nextModelIdx = modelList.FindIndex(x => x == _selectedModel) + 1;
        if (nextModelIdx >= modelList.Count)
        {
            return !hasErrors;
        }

        _selectedModel = modelList[nextModelIdx];
        return false;
    }
}