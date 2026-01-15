using System.Text.Json;
using Microsoft.JSInterop;
using WebChat.Client.Models;

namespace WebChat.Client.Services;

public sealed class LocalStorageService(IJSRuntime jsRuntime)
{
    private const string TopicsKey = "webchat_topics";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<List<StoredTopic>> GetTopicsAsync()
    {
        try
        {
            var json = await jsRuntime.InvokeAsync<string?>("localStorage.getItem", TopicsKey);
            Console.WriteLine($"[LocalStorage] GetTopics raw JSON: {json ?? "null"}");

            if (string.IsNullOrEmpty(json))
            {
                return [];
            }

            var topics = JsonSerializer.Deserialize<List<StoredTopic>>(json, _jsonOptions) ?? [];
            Console.WriteLine($"[LocalStorage] Loaded {topics.Count} topics");
            return topics;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LocalStorage] Error loading topics: {ex.Message}");
            return [];
        }
    }

    public async Task SaveTopicsAsync(List<StoredTopic> topics)
    {
        var json = JsonSerializer.Serialize(topics, _jsonOptions);
        Console.WriteLine($"[LocalStorage] Saving {topics.Count} topics: {json}");
        await jsRuntime.InvokeVoidAsync("localStorage.setItem", TopicsKey, json);
    }
}