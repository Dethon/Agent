using Domain.Memory;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Memory;

public class ConversationWindowRendererTests
{
    [Fact]
    public void Render_WithSingleUserMessage_MarksItAsCurrent()
    {
        var window = new List<ChatMessage>
        {
            new(ChatRole.User, "cold")
        };

        var rendered = ConversationWindowRenderer.Render(window);

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

        var rendered = ConversationWindowRenderer.Render(window);

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
        var rendered = ConversationWindowRenderer.Render([]);
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

        var rendered = ConversationWindowRenderer.Render(window);

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

        var rendered = ConversationWindowRenderer.Render(window);

        rendered.ShouldBe(
            "[context -1] assistant: leading assistant msg\n" +
            "[context -1] user: first user\n" +
            "[context -1] assistant: first reply\n" +
            "[CURRENT]    user: second user");
    }
}
