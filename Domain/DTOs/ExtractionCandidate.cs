namespace Domain.DTOs;

public record ExtractionCandidate(
    string Content,
    MemoryCategory Category,
    double Importance,
    double Confidence,
    IReadOnlyList<string> Tags,
    string? Context);