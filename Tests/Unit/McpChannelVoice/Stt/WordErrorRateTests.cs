using McpChannelVoice.Services.Stt;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Stt;

public class WordErrorRateTests
{
    [Theory]
    [InlineData("enciende la luz de la cocina", "enciende la luz de la cocina", 0.0)]
    [InlineData("apaga la luz roja", "apaga la luz azul", 0.25)]
    [InlineData("Enciende la luz.", "enciende  LA luz", 0.0)]
    public void Compute_ReturnsExpectedRate(string reference, string hypothesis, double expected)
    {
        WordErrorRate.Compute(reference, hypothesis).ShouldBe(expected, 1e-9);
    }
}