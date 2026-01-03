using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Memory;

public class MemoryReflectTool(IMemoryStore store)
{
    private const int MaxSourceMemories = 20;
    private const int MaxSummaryFacts = 3;
    private const int MaxSummarySkills = 2;
    private const int MaxAppreciated = 5;
    private const int MaxAvoidances = 3;
    private const int MaxGuidelines = 10;
    private const int MaxActiveProjects = 5;
    private const double MinMemoriesForFullConfidence = 20.0;
    private const double MinProjectDecayFactor = 0.5;
    private const double HighImportanceThreshold = 0.7;

    protected const string Name = "memory_reflect";

    protected const string Description = """
                                         Synthesizes a personality profile from accumulated memories. Use this to understand
                                         HOW to interact with a user, not just WHAT you know about them.

                                         Call this:
                                         - After storing several new memories
                                         - When starting a new conversation (get the latest synthesis)
                                         - When you're unsure how to tailor your response

                                         The returned profile includes:
                                         - Communication style preferences
                                         - Technical context and expertise
                                         - Concrete interaction guidelines
                                         - Active projects and context

                                         Use the interactionGuidelines to adjust your behavior throughout the conversation.
                                         """;

    protected async Task<JsonNode> Run(
        string userId,
        bool includeMemories = false,
        CancellationToken ct = default)
    {
        var memories = await store.GetByUserIdAsync(userId, ct);
        var activeMemories = memories.Where(m => m.SupersededById is null).ToList();

        if (activeMemories.Count == 0)
        {
            return CreateNoMemoriesResponse(userId);
        }

        var profile = SynthesizeProfile(userId, activeMemories);
        await store.SaveProfileAsync(profile, ct);

        return CreateProfileResponse(userId, profile, activeMemories, includeMemories);
    }

    private static JsonObject CreateNoMemoriesResponse(string userId)
    {
        return new JsonObject
        {
            ["userId"] = userId,
            ["profile"] = null,
            ["message"] = "No memories found for this user. Interact more to build a profile."
        };
    }

    private static JsonObject CreateProfileResponse(
        string userId,
        PersonalityProfile profile,
        List<MemoryEntry> memories,
        bool includeMemories)
    {
        var response = new JsonObject
        {
            ["userId"] = userId,
            ["profile"] = CreateProfileJson(profile),
            ["confidence"] = profile.Confidence,
            ["basedOnMemories"] = profile.BasedOnMemoryCount,
            ["lastReflection"] = profile.LastUpdated.ToString("o")
        };

        if (includeMemories)
        {
            response["sourceMemories"] = CreateSourceMemoriesJson(memories);
        }

        return response;
    }

    private static JsonObject CreateProfileJson(PersonalityProfile profile)
    {
        return new JsonObject
        {
            ["summary"] = profile.Summary,
            ["communicationStyle"] = CreateCommunicationStyleJson(profile.CommunicationStyle),
            ["technicalContext"] = CreateTechnicalContextJson(profile.TechnicalContext),
            ["interactionGuidelines"] = CreateStringArray(profile.InteractionGuidelines),
            ["activeProjects"] = CreateStringArray(profile.ActiveProjects)
        };
    }

    private static JsonNode? CreateCommunicationStyleJson(CommunicationStyle? style)
    {
        return style is null
            ? null
            : new JsonObject
            {
                ["preference"] = style.Preference,
                ["avoidances"] = CreateStringArray(style.Avoidances),
                ["appreciated"] = CreateStringArray(style.Appreciated)
            };
    }

    private static JsonNode? CreateTechnicalContextJson(TechnicalContext? context)
    {
        return context is null
            ? null
            : new JsonObject
            {
                ["expertise"] = CreateStringArray(context.Expertise),
                ["learning"] = CreateStringArray(context.Learning),
                ["stack"] = CreateStringArray(context.Stack)
            };
    }

    private static JsonArray CreateStringArray(IEnumerable<string> items)
    {
        return new JsonArray(items.Select(i => JsonValue.Create(i)).ToArray());
    }

    private static JsonArray CreateSourceMemoriesJson(List<MemoryEntry> memories)
    {
        return new JsonArray(memories
            .OrderByDescending(m => m.Importance)
            .Take(MaxSourceMemories)
            .Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.Id,
                ["category"] = m.Category.ToString().ToLowerInvariant(),
                ["content"] = m.Content,
                ["importance"] = m.Importance
            })
            .ToArray());
    }

    private static PersonalityProfile SynthesizeProfile(string userId, List<MemoryEntry> memories)
    {
        var categorized = new CategorizedMemories(memories);

        return new PersonalityProfile
        {
            UserId = userId,
            Summary = BuildSummary(categorized),
            CommunicationStyle = BuildCommunicationStyle(categorized.Preferences),
            TechnicalContext = BuildTechnicalContext(categorized.Skills, categorized.Facts),
            InteractionGuidelines = BuildGuidelines(categorized.Instructions, categorized.Personality),
            ActiveProjects = BuildActiveProjects(categorized.Projects),
            Confidence = CalculateConfidence(memories),
            BasedOnMemoryCount = memories.Count,
            LastUpdated = DateTimeOffset.UtcNow
        };
    }

    private static string BuildSummary(CategorizedMemories categorized)
    {
        var parts = categorized.Facts
            .OrderByDescending(f => f.Importance)
            .Take(MaxSummaryFacts)
            .Select(f => f.Content)
            .Concat(categorized.Skills
                .OrderByDescending(s => s.Importance)
                .Take(MaxSummarySkills)
                .Select(s => s.Content))
            .ToList();

        return parts.Count > 0
            ? string.Join(". ", parts)
            : "New user with limited interaction history";
    }

    private static CommunicationStyle? BuildCommunicationStyle(List<MemoryEntry> preferences)
    {
        if (preferences.Count == 0)
        {
            return null;
        }

        return new CommunicationStyle
        {
            Preference = preferences.OrderByDescending(p => p.Importance).FirstOrDefault()?.Content,
            Appreciated = FilterByKeywords(preferences, ["prefer", "like"], MaxAppreciated),
            Avoidances = FilterByKeywords(preferences, ["don't", "avoid", "dislike"], MaxAvoidances)
        };
    }

    private static List<string> FilterByKeywords(List<MemoryEntry> memories, string[] keywords, int maxCount)
    {
        return memories
            .Where(m => keywords.Any(k => m.Content.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(m => m.Importance)
            .Take(maxCount)
            .Select(m => m.Content)
            .ToList();
    }

    private static TechnicalContext? BuildTechnicalContext(List<MemoryEntry> skills, List<MemoryEntry> facts)
    {
        if (skills.Count == 0)
        {
            return null;
        }

        return new TechnicalContext
        {
            Expertise = skills
                .Where(s => s.Content.Contains("expert", StringComparison.OrdinalIgnoreCase) ||
                            s.Content.Contains("experienced", StringComparison.OrdinalIgnoreCase) ||
                            s.Importance >= HighImportanceThreshold)
                .Select(s => s.Content)
                .ToList(),
            Learning = skills
                .Where(s => s.Content.Contains("learning", StringComparison.OrdinalIgnoreCase) ||
                            s.Content.Contains("new to", StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Content)
                .ToList(),
            Stack = facts
                .Where(f => f.Content.Contains("uses", StringComparison.OrdinalIgnoreCase) ||
                            f.Tags.Any(t => t.Equals("tech", StringComparison.OrdinalIgnoreCase)))
                .Select(f => f.Content)
                .ToList()
        };
    }

    private static List<string> BuildGuidelines(List<MemoryEntry> instructions, List<MemoryEntry> personality)
    {
        return instructions
            .OrderByDescending(i => i.Importance)
            .Select(i => i.Content)
            .Concat(personality.OrderByDescending(p => p.Importance).Select(p => p.Content))
            .Take(MaxGuidelines)
            .ToList();
    }

    private static List<string> BuildActiveProjects(List<MemoryEntry> projects)
    {
        return projects
            .Where(p => p.DecayFactor > MinProjectDecayFactor)
            .OrderByDescending(p => p.LastAccessedAt)
            .Take(MaxActiveProjects)
            .Select(p => p.Content)
            .ToList();
    }

    private static double CalculateConfidence(List<MemoryEntry> memories)
    {
        return Math.Round(
            Math.Min(1.0, memories.Count / MinMemoriesForFullConfidence) * memories.Average(m => m.Confidence),
            2);
    }

    private sealed class CategorizedMemories
    {
        public List<MemoryEntry> Preferences { get; }
        public List<MemoryEntry> Facts { get; }
        public List<MemoryEntry> Skills { get; }
        public List<MemoryEntry> Projects { get; }
        public List<MemoryEntry> Personality { get; }
        public List<MemoryEntry> Instructions { get; }

        public CategorizedMemories(List<MemoryEntry> memories)
        {
            Preferences = memories.Where(m => m.Category == MemoryCategory.Preference).ToList();
            Facts = memories.Where(m => m.Category == MemoryCategory.Fact).ToList();
            Skills = memories.Where(m => m.Category == MemoryCategory.Skill).ToList();
            Projects = memories.Where(m => m.Category == MemoryCategory.Project).ToList();
            Personality = memories.Where(m => m.Category == MemoryCategory.Personality).ToList();
            Instructions = memories.Where(m => m.Category == MemoryCategory.Instruction).ToList();
        }
    }
}