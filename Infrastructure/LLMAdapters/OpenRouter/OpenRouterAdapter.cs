using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.LLMAdapters.OpenRouter;

public class OpenRouterAdapter(HttpClient client, string model) : ILargeLanguageModel
{
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

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
        };

        var response = await client
            .PostAsJsonAsync("chat/completions", request, jsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        var openRouterResponse = await response.Content
            .ReadFromJsonAsync<OpenRouterResponse>(jsonOptions, cancellationToken);

        return openRouterResponse?.ToAgentResponses() ?? [];
    }
}