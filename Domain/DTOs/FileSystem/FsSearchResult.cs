namespace Domain.DTOs.FileSystem;

public sealed record FsSearchResult
{
    public required string Query { get; init; }
    public required bool Regex { get; init; }
    public required string Path { get; init; }
    public required int FilesSearched { get; init; }
    public required int FilesWithMatches { get; init; }
    public required int TotalMatches { get; init; }
    public required bool Truncated { get; init; }
    public required IReadOnlyList<FsSearchFileResult> Results { get; init; }
}

public sealed record FsSearchFileResult
{
    public required string File { get; init; }
    public int? MatchCount { get; init; }
    public IReadOnlyList<FsSearchMatch>? Matches { get; init; }
}

public sealed record FsSearchMatch
{
    public required int Line { get; init; }
    public required string Text { get; init; }
    public string? Section { get; init; }
    public FsSearchContext? Context { get; init; }
}

public sealed record FsSearchContext
{
    public required IReadOnlyList<string> Before { get; init; }
    public required IReadOnlyList<string> After { get; init; }
}