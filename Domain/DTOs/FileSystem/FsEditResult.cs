namespace Domain.DTOs.FileSystem;

public sealed record FsEditResult
{
    public required string Status { get; init; }
    public required string FilePath { get; init; }
    public required int TotalOccurrencesReplaced { get; init; }
    public required IReadOnlyList<FsEditDetail> Edits { get; init; }
    public string? Note { get; init; }
}

public sealed record FsEditDetail
{
    public required int OccurrencesReplaced { get; init; }
    public required FsLineRange AffectedLines { get; init; }
}

public sealed record FsLineRange
{
    public required int Start { get; init; }
    public required int End { get; init; }
}