namespace Domain.DTOs;

public record MemoryEntry
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required MemoryCategory Category { get; init; }
    public required string Content { get; init; }
    public string? Context { get; init; }
    public required double Importance { get; init; }
    public required double Confidence { get; init; }
    public float[]? Embedding { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastAccessedAt { get; init; }
    public int AccessCount { get; init; }
    public string? SupersededById { get; init; }
    public MemorySource? Source { get; init; }
}

public record MemorySource(string? ConversationId, string? MessageId);

public enum MemoryCategory
{
    Preference,
    Fact,
    Relationship,
    Skill,
    Project,
    Personality,
    Instruction
}