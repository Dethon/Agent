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

        // Pin the heading and the per-satellite "- {id} — {room}" line format, but not the
        // surrounding descriptive prose (which is tunable copy) — and assert input order is preserved
        // (a regression that sorted or reversed the satellites would otherwise slip through).
        result.ShouldContain("## Voice satellites");
        result.ShouldContain("- fran-office-01 — Fran's office");
        result.ShouldContain("- laura-office-01 — Laura's office");
        result.IndexOf("fran-office-01", StringComparison.Ordinal)
            .ShouldBeLessThan(result.IndexOf("laura-office-01", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_NoSatellites_ReturnsEmpty()
    {
        VoicePrompt.Build([]).ShouldBe(string.Empty);
    }
}