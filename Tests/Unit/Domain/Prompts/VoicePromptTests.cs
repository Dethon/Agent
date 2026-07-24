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

    [Fact]
    public void Build_WithSatellites_IsCatalogOnly()
    {
        var result = VoicePrompt.Build([("fran-office-01", "Fran's office")]);

        // voice_prompt is the satellite catalog. MCP prompts are pulled unfiltered into every agent
        // that mounts the voice server for its /timers tools, so any reply-style rule here must be
        // conditioned on the reply being spoken ("aloud") or it governs text channels too. The flat
        // spoken-style paragraph that used to lead this prompt must stay gone.
        result.ShouldStartWith("## Voice satellites\n\nThese are the satellites");
        result.ShouldNotContain("One short sentence");
        result.ShouldNotContain("read on a screen");
        result.ShouldContain("aloud");
    }
}