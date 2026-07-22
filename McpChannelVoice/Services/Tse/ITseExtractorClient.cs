namespace McpChannelVoice.Services.Tse;

public interface ITseExtractorClient
{
    // Extracted 16 kHz mono WAV bytes, or null when extraction is unavailable (service down,
    // deadline expired, unknown speaker, non-success status). Fail-open by contract: only the
    // caller's own cancellation surfaces as an exception.
    Task<byte[]?> ExtractAsync(byte[] mixtureWav, string speaker, CancellationToken ct);
}