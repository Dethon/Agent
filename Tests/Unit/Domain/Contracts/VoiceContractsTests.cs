using Domain.Contracts;
using Domain.DTOs.Voice;
using Shouldly;

namespace Tests.Unit.Domain.Contracts;

public class VoiceContractsTests
{
    [Fact]
    public void ISpeechToText_HasTranscribeAsync()
    {
        var method = typeof(ISpeechToText).GetMethod("TranscribeAsync");
        method.ShouldNotBeNull();
        method!.ReturnType.ShouldBe(typeof(Task<TranscriptionResult>));
    }

    [Fact]
    public void ITextToSpeech_HasSynthesizeAsync()
    {
        var method = typeof(ITextToSpeech).GetMethod("SynthesizeAsync");
        method.ShouldNotBeNull();
        method!.ReturnType.ShouldBe(typeof(IAsyncEnumerable<AudioChunk>));
    }
}