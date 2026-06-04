using McpChannelVoice.Services.Stt;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Stt;

public class WordErrorRateTests
{
    [Fact]
    public void Compute_IdenticalText_IsZero()
    {
        WordErrorRate.Compute("enciende la luz de la cocina", "enciende la luz de la cocina")
            .ShouldBe(0.0, 1e-9);
    }

    [Fact]
    public void Compute_OneWrongWordOutOfFour_IsQuarter()
    {
        WordErrorRate.Compute("apaga la luz roja", "apaga la luz azul").ShouldBe(0.25, 1e-9);
    }

    [Fact]
    public void Compute_IsCaseAndPunctuationInsensitive()
    {
        WordErrorRate.Compute("Enciende la luz.", "enciende  LA luz").ShouldBe(0.0, 1e-9);
    }
}