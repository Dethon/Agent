using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

public class LanguagePromptTests
{
    [Fact]
    public void Build_NoLanguage_ReturnsNull()
    {
        LanguagePrompt.Build(null).ShouldBeNull();
        LanguagePrompt.Build("   ").ShouldBeNull();
    }

    [Theory]
    [InlineData("es")]
    [InlineData("es-ES")]
    [InlineData("ES")]
    [InlineData("spanish")]
    [InlineData("Español")]
    [InlineData("castellano")]
    public void Build_SpanishAlias_RendersTheDirectiveInSpanish(string configured)
    {
        var result = LanguagePrompt.Build(configured);

        result.ShouldNotBeNull();
        result.ShouldStartWith("## Idioma");
        result.ShouldContain("SIEMPRE en español");
    }

    // The reason the section exists: every other part of a request (these instructions, the tool
    // descriptions and results, the recalled memory block, the metadata prefix on each user
    // message) is English, and a relative rule loses to it. The directive has to say outright
    // that reading English does not mean answering in English.
    [Fact]
    public void Build_Spanish_SaysTheEnglishContextDoesNotChangeTheReplyLanguage()
    {
        var result = LanguagePrompt.Build("es")!;

        result.ShouldContain("en inglés");
        result.ShouldContain("NO cambia tu idioma");
    }

    // The first token of a voice turn is the pre-tool acknowledgement word. Once that lands in
    // English the rest of the turn follows it, so the directive names it explicitly.
    [Fact]
    public void Build_Spanish_CoversTheFirstWordSpokenBeforeATool()
    {
        LanguagePrompt.Build("es")!.ShouldContain("la primera palabra");
    }

    // An assistant turn that already drifted stays in the history for the rest of the
    // conversation window; without this the model copies its own mistake.
    [Fact]
    public void Build_Spanish_DisownsAnEarlierDriftedReply()
    {
        LanguagePrompt.Build("es")!.ShouldContain("no lo repitas");
    }

    [Fact]
    public void Build_English_RendersTheDirectiveInEnglish()
    {
        var result = LanguagePrompt.Build("en")!;

        result.ShouldStartWith("## Language");
        result.ShouldContain("always reply in English");
    }

    // A language with no shipped template still gets a directive; the configured value is used
    // verbatim as the language name.
    [Fact]
    public void Build_UnknownLanguage_FallsBackToAnEnglishDirectiveNamingIt()
    {
        var result = LanguagePrompt.Build("Galician")!;

        result.ShouldStartWith("## Language");
        result.ShouldContain("always reply in Galician");
    }
}