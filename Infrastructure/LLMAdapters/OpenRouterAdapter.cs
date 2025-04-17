using System.Net.Http.Json;
using Domain.Contracts;

namespace Infrastructure.LLMAdapters;

public class OpenRouterAdapter(HttpClient client): ILargeLanguageModel
{
    public async Task<string> Prompt(string prompt, CancellationToken cancellationToken=default)
    {
        var request = new OpenRouterRequest
        {
            Prompt = prompt,
        };
        var response = await client.PostAsJsonAsync("ask", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}

internal record OpenRouterRequest
{
    public string Prompt { get; init; } = string.Empty;
}
