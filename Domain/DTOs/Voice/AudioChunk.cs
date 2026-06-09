namespace Domain.DTOs.Voice;

public record AudioChunk
{
    public required ReadOnlyMemory<byte> Data { get; init; }
    public required AudioFormat Format { get; init; }
    public TimeSpan Timestamp { get; init; }
}