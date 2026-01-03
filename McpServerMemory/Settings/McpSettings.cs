namespace McpServerMemory.Settings;

public record McpSettings
{
    public required string RedisConnectionString { get; init; }
    public required OpenRouterSettings OpenRouter { get; init; }
    public EmbeddingSettings Embedding { get; init; } = new();
}

public record OpenRouterSettings
{
    public required string ApiKey { get; init; }
    public string BaseUrl { get; init; } = "https://openrouter.ai/api/v1/";
}

public class EmbeddingSettings
{
    public string Model { get; set; } = "openai/text-embedding-3-small";
}