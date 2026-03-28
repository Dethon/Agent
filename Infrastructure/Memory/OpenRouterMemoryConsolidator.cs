using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Memory;

public class OpenRouterMemoryConsolidator(
    IChatClient chatClient,
    ILogger<OpenRouterMemoryConsolidator> logger) : IMemoryConsolidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public async Task<IReadOnlyList<MergeDecision>> ConsolidateAsync(
        IReadOnlyList<MemoryEntry> memories, CancellationToken ct)
    {
        var summary = string.Join("\n", memories.Select(m =>
            $"- [{m.Id}] ({m.Category.ToString().ToLowerInvariant()}) {m.Content} (importance: {m.Importance:F1}, created: {m.CreatedAt:yyyy-MM-dd})"));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, MemoryPrompts.ConsolidationSystemPrompt),
            new(ChatRole.User, summary)
        };

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
        return ParseMergeDecisions(response.Text);
    }

    public async Task<PersonalityProfile> SynthesizeProfileAsync(
        string userId, IReadOnlyList<MemoryEntry> memories, CancellationToken ct)
    {
        var summary = string.Join("\n", memories.Select(m =>
            $"- ({m.Category.ToString().ToLowerInvariant()}) {m.Content} (importance: {m.Importance:F1})"));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, MemoryPrompts.ProfileSynthesisSystemPrompt),
            new(ChatRole.User, summary)
        };

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
        return ParseProfile(userId, memories.Count, response.Text);
    }

    private IReadOnlyList<MergeDecision> ParseMergeDecisions(string responseText)
    {
        try
        {
            var json = StripCodeFences(responseText);
            var dtos = JsonSerializer.Deserialize<List<MergeDecisionDto>>(json, JsonOptions);
            if (dtos is null) return [];

            return dtos
                .Select(d => new MergeDecision(
                    d.SourceIds ?? [],
                    d.Action,
                    d.MergedContent,
                    d.Category,
                    d.Importance,
                    d.Tags))
                .ToList();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse consolidation response: {Response}",
                responseText.Length > 200 ? responseText[..200] : responseText);
            return [];
        }
    }

    private PersonalityProfile ParseProfile(string userId, int memoryCount, string responseText)
    {
        try
        {
            var json = StripCodeFences(responseText);
            var dto = JsonSerializer.Deserialize<ProfileDto>(json, JsonOptions);
            if (dto is null) return EmptyProfile(userId, memoryCount);

            return new PersonalityProfile
            {
                UserId = userId,
                Summary = dto.Summary ?? string.Empty,
                CommunicationStyle = dto.CommunicationStyle is null ? null : new CommunicationStyle
                {
                    Preference = dto.CommunicationStyle.Preference,
                    Avoidances = dto.CommunicationStyle.Avoidances ?? [],
                    Appreciated = dto.CommunicationStyle.Appreciated ?? []
                },
                TechnicalContext = dto.TechnicalContext is null ? null : new TechnicalContext
                {
                    Expertise = dto.TechnicalContext.Expertise ?? [],
                    Learning = dto.TechnicalContext.Learning ?? [],
                    Stack = dto.TechnicalContext.Stack ?? []
                },
                InteractionGuidelines = dto.InteractionGuidelines ?? [],
                ActiveProjects = dto.ActiveProjects ?? [],
                Confidence = Math.Min(1.0, (double)memoryCount / 20),
                BasedOnMemoryCount = memoryCount,
                LastUpdated = DateTimeOffset.UtcNow
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse profile synthesis response: {Response}",
                responseText.Length > 200 ? responseText[..200] : responseText);
            return EmptyProfile(userId, memoryCount);
        }
    }

    private static PersonalityProfile EmptyProfile(string userId, int memoryCount) => new()
    {
        UserId = userId,
        Summary = string.Empty,
        Confidence = Math.Min(1.0, (double)memoryCount / 20),
        BasedOnMemoryCount = memoryCount,
        LastUpdated = DateTimeOffset.UtcNow
    };

    private static string StripCodeFences(string text)
    {
        var json = text.Trim();
        if (!json.StartsWith("```")) return json;

        var firstNewline = json.IndexOf('\n');
        var lastFence = json.LastIndexOf("```");
        if (firstNewline >= 0 && lastFence > firstNewline)
            return json[(firstNewline + 1)..lastFence].Trim();

        return json;
    }

    private sealed record MergeDecisionDto
    {
        public IReadOnlyList<string>? SourceIds { get; init; }
        public MergeAction Action { get; init; }
        public string? MergedContent { get; init; }
        public MemoryCategory? Category { get; init; }
        public double? Importance { get; init; }
        public IReadOnlyList<string>? Tags { get; init; }
    }

    private sealed record ProfileDto
    {
        public string? Summary { get; init; }
        public CommunicationStyleDto? CommunicationStyle { get; init; }
        public TechnicalContextDto? TechnicalContext { get; init; }
        public IReadOnlyList<string>? InteractionGuidelines { get; init; }
        public IReadOnlyList<string>? ActiveProjects { get; init; }
    }

    private sealed record CommunicationStyleDto
    {
        public string? Preference { get; init; }
        public IReadOnlyList<string>? Avoidances { get; init; }
        public IReadOnlyList<string>? Appreciated { get; init; }
    }

    private sealed record TechnicalContextDto
    {
        public IReadOnlyList<string>? Expertise { get; init; }
        public IReadOnlyList<string>? Learning { get; init; }
        public IReadOnlyList<string>? Stack { get; init; }
    }
}
