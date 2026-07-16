namespace Domain.DTOs.Voice;

public record TranscriptionResult
{
    public required string Text { get; init; }
    public string? Language { get; init; }
    public double? Confidence { get; init; }

    // Raw whisper quality signals (duration-weighted per transcription by the patched
    // wyoming-whisper server). Recorded on metrics for threshold calibration; only
    // Confidence gates dispatch. Null when the server doesn't emit them (fail-open).
    public double? AvgLogProb { get; init; }
    public double? NoSpeechProb { get; init; }
    public double? CompressionRatio { get; init; }
}