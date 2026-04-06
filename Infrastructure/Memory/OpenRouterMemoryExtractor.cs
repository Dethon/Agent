using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Domain.Memory;
using Domain.Prompts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Memory;

public class OpenRouterMemoryExtractor(
    IChatClient chatClient,
    IMemoryStore store,
    ILogger<OpenRouterMemoryExtractor> logger) : IMemoryExtractor
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly ChatOptions _extractionChatOptions = new()
    {
        Instructions = MemoryPrompts.ExtractionSystemPrompt,
        ResponseFormat = ChatResponseFormat.ForJsonSchema<ExtractionResponseDto>(
            serializerOptions: _jsonOptions)
    };

    public async Task<IReadOnlyList<ExtractionCandidate>> ExtractAsync(
        IReadOnlyList<ChatMessage> contextWindow, string userId, CancellationToken ct)
    {
        if (contextWindow.Count == 0)
        {
            return [];
        }

        var profile = await store.GetProfileAsync(userId, ct);
        var renderedWindow = ConversationWindowRenderer.Render(contextWindow);

        var userPrompt = profile is not null
            ? $"Existing user profile:\n{profile.Summary}\n\nConversation window:\n{renderedWindow}"
            : $"Conversation window:\n{renderedWindow}";

        var userMessage = new ChatMessage(ChatRole.User, userPrompt);
        userMessage.SetSenderId(userId);

        var messages = new List<ChatMessage> { userMessage };

        var response = await chatClient.GetResponseAsync(messages, _extractionChatOptions, ct);
        return ParseCandidates(response.Text);
    }

    private IReadOnlyList<ExtractionCandidate> ParseCandidates(string responseText)
    {
        try
        {
            var json = StripCodeFences(responseText);
            var wrapper = JsonSerializer.Deserialize<ExtractionResponseDto>(json, _jsonOptions);

            return wrapper?.Candidates?
                .Select(c => new ExtractionCandidate(
                    c.Content,
                    c.Category,
                    Math.Clamp(c.Importance, 0, 1),
                    Math.Clamp(c.Confidence, 0, 1),
                    c.Tags ?? [],
                    c.Context))
                .ToList() ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse extraction response: {Response}",
                responseText.Length > 200 ? responseText[..200] : responseText);
            return [];
        }
    }

    private static string StripCodeFences(string text)
    {
        var json = text.Trim();
        if (!json.StartsWith("```"))
        {
            return json;
        }


        var firstNewline = json.IndexOf('\n');
        var lastFence = json.LastIndexOf("```");
        return firstNewline >= 0 && lastFence > firstNewline
            ? json[(firstNewline + 1)..lastFence].Trim()
            : json;
    }

    private sealed record ExtractionResponseDto
    {
        public List<ExtractionCandidateDto>? Candidates { get; init; }
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
