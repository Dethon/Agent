using Domain.DTOs.Voice;

namespace Domain.Contracts;

public interface ITextToSpeech
{
    IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SynthesisOptions options,
        CancellationToken ct);
}