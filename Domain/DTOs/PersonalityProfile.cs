namespace Domain.DTOs;

public record PersonalityProfile
{
    public required string UserId { get; init; }
    public required string Summary { get; init; }
    public CommunicationStyle? CommunicationStyle { get; init; }
    public TechnicalContext? TechnicalContext { get; init; }
    public IReadOnlyList<string> InteractionGuidelines { get; init; } = [];
    public IReadOnlyList<string> ActiveProjects { get; init; } = [];
    public double Confidence { get; init; }
    public int BasedOnMemoryCount { get; init; }
    public required DateTimeOffset LastUpdated { get; init; }
}

public record CommunicationStyle
{
    public string? Preference { get; init; }
    public IReadOnlyList<string> Avoidances { get; init; } = [];
    public IReadOnlyList<string> Appreciated { get; init; } = [];
}

public record TechnicalContext
{
    public IReadOnlyList<string> Expertise { get; init; } = [];
    public IReadOnlyList<string> Learning { get; init; } = [];
    public IReadOnlyList<string> Stack { get; init; } = [];
}