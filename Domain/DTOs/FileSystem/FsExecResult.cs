namespace Domain.DTOs.FileSystem;

public sealed record FsExecResult
{
    public required string Stdout { get; init; }
    public required string Stderr { get; init; }
    public required int ExitCode { get; init; }
    public required bool Truncated { get; init; }
    public required bool TimedOut { get; init; }
    public required long DurationMs { get; init; }
    public required string Cwd { get; init; }
}