using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Memory;

public class OpenRouterMemoryExtractor(
    IChatClient chatClient,
    IMemoryStore store,
    ILogger<OpenRouterMemoryExtractor> logger) : IMemoryExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly ChatOptions ExtractionChatOptions = new()
    {
        ResponseFormat = ChatResponseFormat.Json
    };

    public async Task<IReadOnlyList<ExtractionCandidate>> ExtractAsync(
        string messageContent, string userId, CancellationToken ct)
    {
        var profile = await store.GetProfileAsync(userId, ct);

        var userPrompt = profile is not null
            ? $"Existing user profile:\n{profile.Summary}\n\nMessage to analyze:\n{messageContent}"
            : $"Message to analyze:\n{messageContent}";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, MemoryPrompts.ExtractionSystemPrompt),
            new(ChatRole.User, userPrompt)
        };

        var response = await chatClient.GetResponseAsync(messages, ExtractionChatOptions, ct);
        var responseText = response.Text;

        return ParseCandidates(responseText);
    }

    private IReadOnlyList<ExtractionCandidate> ParseCandidates(string responseText)
    {
        try
        {
            var json = responseText.Trim();
            if (json.StartsWith("```"))
            {
                var firstNewline = json.IndexOf('\n');
                var lastFence = json.LastIndexOf("```");
                if (firstNewline >= 0 && lastFence > firstNewline)
                {
                    json = json[(firstNewline + 1)..lastFence].Trim();
                }
            }

            var candidates = JsonSerializer.Deserialize<List<ExtractionCandidateDto>>(json, JsonOptions);
            if (candidates is null)
            {
                return [];
            }

            return candidates
                .Select(c => new ExtractionCandidate(
                    c.Content,
                    c.Category,
                    Math.Clamp(c.Importance, 0, 1),
                    Math.Clamp(c.Confidence, 0, 1),
                    c.Tags ?? [],
                    c.Context))
                .ToList();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse extraction response: {Response}",
                responseText.Length > 200 ? responseText[..200] : responseText);
            return [];
        }
    }

    private sealed record ExtractionCandidateDto
    {
        public required string Content { get; init; }
        public required MemoryCategory Category { get; init; }
        public double Importance { get; init; }
        public double Confidence { get; init; }
        public IReadOnlyList<string>? Tags { get; init; }
        public string? Context { get; init; }
    }
}
