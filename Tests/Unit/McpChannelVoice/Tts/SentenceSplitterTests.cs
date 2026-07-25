using McpChannelVoice.Services.Tts;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Tts;

public class SentenceSplitterTests
{
    [Fact]
    public void TryTake_NoTerminator_TakesNothing()
    {
        SentenceSplitter.TryTake("Mañana por la tarde hará", 10, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryTake_CompleteSentenceOverThreshold_TakesIt()
    {
        SentenceSplitter.TryTake("Hará sol por la tarde. Y algo de", 10, out var speakable, out var remainder)
            .ShouldBeTrue();

        speakable.ShouldBe("Hará sol por la tarde.");
        remainder.ShouldBe("Y algo de");
    }

    [Fact]
    public void TryTake_CompleteSentenceUnderThreshold_WaitsForMore()
    {
        // Synthesizing "Sí." on its own costs a whole TTS round trip and lands an audible gap
        // before the rest, so a boundary under the threshold is deliberately not a flush point.
        SentenceSplitter.TryTake("Sí. Ahora mismo", 40, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryTake_SeveralCompleteSentences_TakesThroughTheLastBoundary()
    {
        // Greedy to the last boundary: fewer, larger TTS requests beat many small ones, and
        // cross-sentence prosody survives inside a single request.
        SentenceSplitter.TryTake("Uno. Dos. Tres. Y cua", 5, out var speakable, out var remainder)
            .ShouldBeTrue();

        speakable.ShouldBe("Uno. Dos. Tres.");
        remainder.ShouldBe("Y cua");
    }

    [Fact]
    public void TryTake_TerminatorAtEndOfBuffer_IsABoundary()
    {
        SentenceSplitter.TryTake("Ya está encendida la luz.", 5, out var speakable, out var remainder)
            .ShouldBeTrue();

        speakable.ShouldBe("Ya está encendida la luz.");
        remainder.ShouldBe("");
    }

    [Theory]
    [InlineData("El total es 1.234,56 euros y algo")]
    [InlineData("Son 3.5 grados esta noche en casa")]
    public void TryTake_DecimalPoint_IsNotABoundary(string buffer)
    {
        // A '.' between digits never ends a sentence; the whitespace requirement is what excludes it.
        SentenceSplitter.TryTake(buffer, 5, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryTake_Abbreviation_IsNotABoundary()
    {
        SentenceSplitter.TryTake("Viene el Sr. García a las cinco.", 5, out var speakable, out var remainder)
            .ShouldBeTrue();

        speakable.ShouldBe("Viene el Sr. García a las cinco.");
        remainder.ShouldBe("");
    }

    [Fact]
    public void TryTake_Initial_IsNotABoundary()
    {
        SentenceSplitter.TryTake("Te llama J. Crespo desde el salón.", 5, out var speakable, out _)
            .ShouldBeTrue();

        speakable.ShouldBe("Te llama J. Crespo desde el salón.");
    }

    [Fact]
    public void TryTake_SpanishQuestion_SplitsAfterTheClosingMark()
    {
        SentenceSplitter.TryTake("¿Quieres que la apague? Dime cuán", 5, out var speakable, out var remainder)
            .ShouldBeTrue();

        speakable.ShouldBe("¿Quieres que la apague?");
        remainder.ShouldBe("Dime cuán");
    }

    [Fact]
    public void TryTake_Ellipsis_SplitsAfterTheWholeRun()
    {
        SentenceSplitter.TryTake("Espera... ya voy", 5, out var speakable, out var remainder)
            .ShouldBeTrue();

        speakable.ShouldBe("Espera...");
        remainder.ShouldBe("ya voy");
    }

    [Fact]
    public void TryTake_OnlyWhitespaceAfterTerminator_LeavesEmptyRemainder()
    {
        SentenceSplitter.TryTake("Hecho.   ", 5, out var speakable, out var remainder).ShouldBeTrue();

        speakable.ShouldBe("Hecho.");
        remainder.ShouldBe("");
    }
}