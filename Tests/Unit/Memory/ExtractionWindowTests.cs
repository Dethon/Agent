using Domain.Memory;
using Domain.Prompts;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Memory;

public class ExtractionWindowTests
{
    private const string CurrentTurnLabel = "[CURRENT]";
    private const string NearestContextTurnPrefix = "[context -1]";

    private static MemoryAnchor Anchor(int persistedMessageCount) =>
        MemoryAnchor.TakenBeforeCurrentTurnIsPersisted(persistedMessageCount);

    [Fact]
    public void Build_FromHistoryAndFallback_CutsAtTheAnchorAndAppendsTheFallback()
    {
        var history = new ChatMessage[]
        {
            new(ChatRole.User, "turn1 user"),
            new(ChatRole.Assistant, "turn1 assistant"),
            new(ChatRole.User, "turn2 user"),
            new(ChatRole.Assistant, "turn2 assistant"),
            new(ChatRole.User, "turn3 user"),
            new(ChatRole.Assistant, "turn3 assistant"),
            new(ChatRole.User, "turn4 user"),
            new(ChatRole.Assistant, "turn4 assistant"),
            new(ChatRole.User, "turn5 user (drift)")
        };

        // Six messages were persisted when the anchor was taken; the fallback is the current
        // user message, so it claims one of the six slots and five context ones remain.
        var window = ExtractionWindow.Build(history, Anchor(6), "turn4 user", windowSize: 6);

        window.Count.ShouldBe(6);
        window[0].Text.ShouldBe("turn1 assistant");
        window[^1].Text.ShouldBe("turn4 user");
        window[^1].Role.ShouldBe(ChatRole.User);
        window.ShouldNotContain(m => m.Text == "turn4 assistant");
        window.ShouldNotContain(m => m.Text == "turn5 user (drift)");
    }

    [Fact]
    public void Build_WithNoHistoryAndNoFallback_IsEmpty()
    {
        var window = ExtractionWindow.Build(null, Anchor(0), fallbackContent: null, windowSize: 6);

        window.ShouldBeEmpty();
    }

    [Fact]
    public void Build_WithAnchorBeyondTheHistory_UsesWhatIsThere()
    {
        var history = new ChatMessage[] { new(ChatRole.User, "only message") };

        var window = ExtractionWindow.Build(history, Anchor(99), "current message", windowSize: 6);

        window.Count.ShouldBe(2);
        window[0].Text.ShouldBe("only message");
        window[^1].Text.ShouldBe("current message");
    }

    [Fact]
    public void Build_WithNoHistory_IsTheFallbackAlone()
    {
        var window = ExtractionWindow.Build(null, Anchor(0), "I work at Contoso", windowSize: 6);

        window.Count.ShouldBe(1);
        window[0].Text.ShouldBe("I work at Contoso");
        window[0].Role.ShouldBe(ChatRole.User);
    }

    [Fact]
    public void Render_WithSingleUserMessage_MarksItAsCurrent()
    {
        var window = new List<ChatMessage>
        {
            new(ChatRole.User, "cold")
        };

        var rendered = ExtractionWindow.Render(window);

        rendered.ShouldBe("[CURRENT]    user: cold");
    }

    [Fact]
    public void Render_WithMixedTurns_UsesRelativeContextOffsets()
    {
        var window = new List<ChatMessage>
        {
            new(ChatRole.User, "I've been thinking about moving"),
            new(ChatRole.Assistant, "Any particular destination?"),
            new(ChatRole.User, "Portugal, probably"),
            new(ChatRole.Assistant, "Lisbon or somewhere quieter?"),
            new(ChatRole.User, "Lisbon, next spring")
        };

        var rendered = ExtractionWindow.Render(window);

        rendered.ShouldBe(
            "[context -2] user: I've been thinking about moving\n" +
            "[context -2] assistant: Any particular destination?\n" +
            "[context -1] user: Portugal, probably\n" +
            "[context -1] assistant: Lisbon or somewhere quieter?\n" +
            "[CURRENT]    user: Lisbon, next spring");
    }

    [Fact]
    public void Render_WithEmptyWindow_ReturnsEmptyString()
    {
        var rendered = ExtractionWindow.Render([]);
        rendered.ShouldBe(string.Empty);
    }

    [Fact]
    public void Render_WithAssistantAsFinalMessage_StillMarksFinalAsCurrent()
    {
        // Defensive: the renderer doesn't enforce that the last message is a user turn.
        // The caller (extraction worker) guarantees it, but the renderer stays general.
        var window = new List<ChatMessage>
        {
            new(ChatRole.User, "hi"),
            new(ChatRole.Assistant, "hello")
        };

        var rendered = ExtractionWindow.Render(window);

        rendered.ShouldBe(
            "[context -1] user: hi\n" +
            "[CURRENT]    assistant: hello");
    }

    [Fact]
    public void Render_GroupsTurnsByUserTurnBoundary()
    {
        var window = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "leading assistant msg"),
            new(ChatRole.User, "first user"),
            new(ChatRole.Assistant, "first reply"),
            new(ChatRole.User, "second user")
        };

        var rendered = ExtractionWindow.Render(window);

        rendered.ShouldBe(
            "[context -1] assistant: leading assistant msg\n" +
            "[context -1] user: first user\n" +
            "[context -1] assistant: first reply\n" +
            "[CURRENT]    user: second user");
    }

    [Fact]
    public void Render_UsesTheMarkersTheExtractionPromptNames()
    {
        // The prompt tells the extractor to read [CURRENT] and to treat [context -N] as
        // disambiguation only. Renaming a marker on one side and not the other would leave
        // the extractor pulling facts from the wrong turn, silently.
        var window = new List<ChatMessage>
        {
            new(ChatRole.User, "context turn"),
            new(ChatRole.User, "current turn")
        };

        var rendered = ExtractionWindow.Render(window);

        rendered.ShouldContain(CurrentTurnLabel);
        rendered.ShouldContain(NearestContextTurnPrefix);
        MemoryPrompts.ExtractionSystemPrompt.ShouldContain(CurrentTurnLabel);
        MemoryPrompts.ExtractionSystemPrompt.ShouldContain(NearestContextTurnPrefix);
    }
}