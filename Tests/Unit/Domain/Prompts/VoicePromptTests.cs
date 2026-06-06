using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

public class VoicePromptTests
{
    [Fact]
    public void Build_WithSatellites_ListsIdAndRoomInOrder()
    {
        var result = VoicePrompt.Build(
        [
            ("fran-office-01", "Fran's office"),
            ("laura-office-01", "Laura's office")
        ]);

        result.ShouldBe(
            "## Voice satellites\n\n" +
            "These are the voice satellites you can be heard on — the spoken devices placed around the home. Each entry is a stable satellite id and the room it's in:\n\n" +
            "- fran-office-01 — Fran's office\n" +
            "- laura-office-01 — Laura's office\n\n" +
            "Each incoming message tells you which satellite and room it came from, so you can tailor answers to where the person is.");
    }

    [Fact]
    public void Build_NoSatellites_ReturnsEmpty()
    {
        VoicePrompt.Build([]).ShouldBe(string.Empty);
    }

    [Fact]
    public void Build_SingleSatellite_RendersOneBullet()
    {
        var result = VoicePrompt.Build([("office-01", "Office")]);

        result.ShouldContain("## Voice satellites");
        result.ShouldContain("- office-01 — Office");
    }
}